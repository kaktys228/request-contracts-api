using Humanizer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using QRCoder;
using System.Globalization;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xceed.Document.NET;
using Xceed.Drawing;
using Xceed.Words.NET;
using static APIdIplom.Controllers.ExternalApiController;
using static APIdIplom.Controllers.RequestsController;
using Font = Xceed.Document.NET.Font;
using DocumentFormat.OpenXml.Spreadsheet;
using System.IO.Compression;
using Color = Xceed.Drawing.Color;
using DocumentFormat.OpenXml.Wordprocessing;

namespace APIdIplom.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class contractsController : ControllerBase
    {
        private readonly string _connectionString;

        public contractsController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }



        [HttpPost("contracts")]
        public async Task<IActionResult> AddContract([FromBody] CreateContractDto dto)
        {
            if (dto.RequestId <= 0)
                return BadRequest("Некорректный номер заявки");

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = await connection.BeginTransactionAsync();

            try
            {
                // Проверка существования заявки
                // Проверка: есть ли уже контракт по этой заявке
                var checkContractCmd = new NpgsqlCommand("SELECT contractid FROM contracts WHERE requestid = @RequestId", connection, transaction);
                checkContractCmd.Parameters.AddWithValue("@RequestId", dto.RequestId);
                var existingContractId = await checkContractCmd.ExecuteScalarAsync();

                if (existingContractId != null && existingContractId != DBNull.Value)
                {
                    return Conflict($"Контракт по заявке №{dto.RequestId} уже существует (ContractID = {existingContractId}).");
                }

              

                // Подсчёт суммы по заявке (PlannedAmount)
                var sumCmd = new NpgsqlCommand("SELECT SUM(quantity * unitprice) FROM requestitems WHERE requestid = @RequestId", connection, transaction);
                sumCmd.Parameters.AddWithValue("@RequestId", dto.RequestId);
                var sumResult = await sumCmd.ExecuteScalarAsync();
                decimal plannedAmount = sumResult == DBNull.Value ? 0 : Convert.ToDecimal(sumResult);

                // Вставка контракта (ActualAmount = NULL)
                const string insertSql = @"
INSERT INTO contracts
    (requestid,
     statusid,
     bankaccountsid,
     contractnumber,
     plannedamount,
     actualamount,
     contactphone,
     description,
     ikz,
     protocolnumber, 
     protocoldate)
VALUES
    (@RequestId,
     @StatusId,
     1,
     @ContractNumber,
     @PlannedAmount,
     NULL,
     @ContactPhone,
     @Description,
     @IKZ,
     @ProtocolNumber,
     @ProtocolDate)
RETURNING contractid;
";


                int contractId;
                using (var cmd = new NpgsqlCommand(insertSql, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@RequestId", dto.RequestId);
                    cmd.Parameters.AddWithValue("@StatusId", dto.StatusId);
                    cmd.Parameters.AddWithValue("@ContractNumber", dto.ContractNumber);
                    cmd.Parameters.AddWithValue("@PlannedAmount", plannedAmount);
                    cmd.Parameters.AddWithValue("@ContactPhone", dto.ContactPhone ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@Description", dto.Description ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@IKZ", dto.IKZ ?? (object)DBNull.Value); // 👈 добавили IKZ
                    cmd.Parameters.AddWithValue("@ProtocolNumber", (object?)dto.ProtocolNumber ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ProtocolDate", dto.ProtocolDate.HasValue
                                                                ? (object)dto.ProtocolDate.Value
                                                                : DBNull.Value);

                    contractId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }
                // Обновление связи в таблице requestitems
                var updateItemsQuery = @"
            UPDATE requestitems
            SET contractid = @ContractId
            WHERE requestid = @RequestId;
        ";

                using (var updateCmd = new NpgsqlCommand(updateItemsQuery, connection, transaction))
                {
                    updateCmd.Parameters.AddWithValue("@ContractId", contractId);
                    updateCmd.Parameters.AddWithValue("@RequestId", dto.RequestId);
                    await updateCmd.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
                return Ok(new { ContractId = contractId });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, "Ошибка при добавлении контракта: " + ex.Message);
            }
        }

        [HttpPut("update-item-price/{requestItemId}")]
        public async Task<IActionResult> UpdateItemPrice(int requestItemId, [FromBody] decimal newPrice)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            try
            {
                // Выполняем запрос на обновление цены товара по его ID
                var updatePriceQuery = @"
            UPDATE requestitems
            SET unitprice = @NewPrice
            WHERE requestitemid = @RequestItemId;
        ";

                using var cmd = new NpgsqlCommand(updatePriceQuery, connection);
                cmd.Parameters.AddWithValue("@NewPrice", newPrice);
                cmd.Parameters.AddWithValue("@RequestItemId", requestItemId);

                var affectedRows = await cmd.ExecuteNonQueryAsync();

                if (affectedRows == 0)
                {
                    return NotFound($"Товар с ID {requestItemId} не найден.");
                }

                return Ok("Цена успешно обновлена.");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Ошибка при обновлении цены: {ex.Message}");
            }
        }





        [HttpGet("qrcode/{id}")]
        public IActionResult GetContractQrCode(int id)
        {
            try
            {

                var qrGenerator = new QRCodeGenerator();
                var qrData = qrGenerator.CreateQrCode(id.ToString(), QRCodeGenerator.ECCLevel.Q);
                var qrCode = new PngByteQRCode(qrData);
                var qrBytes = qrCode.GetGraphic(20); // 20 — размер точек

                var base64 = Convert.ToBase64String(qrBytes);
                return Ok(new { QrBase64 = base64 });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Ошибка генерации QR-кода: {ex.Message}");
            }
        }




        public class CreateContractDto
        {
            public int RequestId { get; set; }
            public int StatusId { get; set; }
            public string ContractNumber { get; set; }
            public string? ContactPhone { get; set; }
            public string? Description { get; set; } // ← Добавлено
            public string? IKZ { get; set; } // 🔹 Добавлено для поддержки ИКЗ
            public string? ProtocolNumber { get; set; }
            public DateTime? ProtocolDate { get; set; }

        }


        [HttpGet("statuses")]
        public async Task<IActionResult> GetContractStatuses()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var statuses = new List<StatusContract>();
            var query = "SELECT statusid, name FROM statuses_contract";

            using var cmd = new NpgsqlCommand(query, connection);
            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                statuses.Add(new StatusContract
                {
                    StatusID = reader.GetInt32(0),
                    Name = reader.GetString(1)
                });
            }

            return Ok(statuses);
        }

        public class StatusContract
        {
            public int StatusID { get; set; }
            public string Name { get; set; }
        }


        [HttpGet("with-details")]
        public async Task<IActionResult> GetContractsWithDetails()
        {
            var contracts = new List<ContractDto>();

            var query = @"
        SELECT 
            c.contractid,
            c.contractnumber,
            c.contractdate,
            s.name AS statusname,
            c.requestid,
            sup.name AS suppliername,
            c.plannedamount,
            c.manual_request_photo,
            c.actualamount
        FROM contracts c
        JOIN statuses_contract s ON c.statusid = s.statusid
        LEFT JOIN suppliers sup ON c.supplierid = sup.supplierid
        ORDER BY c.contractdate DESC;
    ";

            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                using var cmd = new NpgsqlCommand(query, connection);
                using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    byte[]? photoBytes = !reader.IsDBNull(7) ? (byte[])reader[7] : null;

                    contracts.Add(new ContractDto
                    {
                        ContractID = reader.GetInt32(0),
                        ContractNumber = reader.GetString(1),
                        ContractDate = reader.GetDateTime(2),
                        StatusName = reader.GetString(3),
                        RequestID = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
                        SupplierName = reader.IsDBNull(5) ? "" : reader.GetString(5),
                        PlannedAmount = reader.IsDBNull(6) ? 0 : reader.GetDecimal(6),
                        ManualRequestPhotoBase64 = photoBytes != null ? Convert.ToBase64String(photoBytes) : null,
                        ActualAmount = reader.IsDBNull(8) ? 0 : reader.GetDecimal(8)
                    });
                }

                return Ok(contracts);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Ошибка получения контрактов: {ex.Message}");
            }
        }


        public class ContractDto
        {
            public int ContractID { get; set; }
            public string ContractNumber { get; set; }
            public DateTime ContractDate { get; set; }
            public string StatusName { get; set; }
            public int? RequestID { get; set; }
            public string SupplierName { get; set; }
            public decimal PlannedAmount { get; set; }
            public decimal ActualAmount { get; set; } // 👈 новое поле

            public string? ManualRequestPhotoBase64 { get; set; } // 👈 новое поле

        }

        [HttpPost("export-to-word")]
        public async Task<IActionResult> ExportToWord([FromBody] ExportContractDto dto)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // 1. Получаем номер контракта и дату отдельно (всегда сработает, даже если нет товаров)
            string contractNumber = "", supplier = "";
            DateTime contractDate = DateTime.Today;

            var contractInfoCmd = new NpgsqlCommand(@"
    SELECT c.contractnumber, c.contractdate, s.name
    FROM contracts c
    LEFT JOIN suppliers s ON c.supplierid = s.supplierid
    WHERE c.contractid = @contractId", connection);

            contractInfoCmd.Parameters.AddWithValue("@contractId", dto.ContractId);

            using (var contractReader = await contractInfoCmd.ExecuteReaderAsync())
            {
                if (await contractReader.ReadAsync())
                {
                    contractNumber = contractReader.GetString(0);
                    contractDate = contractReader.GetDateTime(1);
                    supplier = contractReader.IsDBNull(2) ? "—" : contractReader.GetString(2);
                }
                else
                {
                    return NotFound("Контракт не найден.");
                }
            }

            // 2. Загружаем товары по контракту
            var query = @"
SELECT 
    c.contractnumber,
    c.contractdate,
    s.name AS suppliername,
    p.name AS productname,
    ch.name AS characteristicname,
    ric.valuerequest,
    u.unitname,
    ri.quantity
FROM requestitems ri
JOIN contracts c ON ri.contractid = c.contractid -- ✔ связь по contractid
LEFT JOIN suppliers s ON c.supplierid = s.supplierid
JOIN products p ON ri.productid = p.productid
JOIN units u ON p.unitid = u.unitid
LEFT JOIN requestitemcharacteristics ric ON ri.requestitemid = ric.requestitemid
LEFT JOIN productcharacteristics pc ON ric.productcharacteristicid = pc.productcharacteristicid
LEFT JOIN characteristics ch ON pc.characteristicid = ch.characteristicid
WHERE c.contractid = @contractId
ORDER BY p.name;


";

            var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@contractId", dto.ContractId);

            var reader = await cmd.ExecuteReaderAsync();
            var contractData = new List<ContractExportRow>();

            while (await reader.ReadAsync())
            {
                contractData.Add(new ContractExportRow
                {
                    ProductName = reader.GetString(3),
                    Characteristic = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    Value = reader.IsDBNull(5) ? "" : reader.GetString(5),
                    Unit = reader.IsDBNull(6) ? "" : reader.GetString(6),
                    Quantity = reader.GetInt32(7)
                });
            }

            reader.Close();

            string technicalText = dto.Law == "223-ФЗ" ? GetTechnicalText223() : GetTechnicalText44();

            // 3. Создание Word-документа
            var doc = DocX.Create("ТЗ.docx");
            doc.InsertParagraph($"Приложение № 5").Alignment = Xceed.Document.NET.Alignment.right;
            doc.InsertParagraph($"к Контракту № {contractNumber}").Alignment = Xceed.Document.NET.Alignment.right;
            doc.InsertParagraph($"от  «{contractDate:dd}» {contractDate:MMMM} {contractDate:yyyy} г.").Alignment = Xceed.Document.NET.Alignment.right;

            doc.InsertParagraph("ТЕХНИЧЕСКОЕ ЗАДАНИЕ")
                .Font("Times New Roman").FontSize(16).Bold().Color(Color.Black)
                .Alignment = Xceed.Document.NET.Alignment.center;
            doc.InsertParagraph().SpacingAfter(10);

            foreach (var paragraph in technicalText.Split("\n"))
            {
                var trimmed = paragraph.Trim();
                if (Regex.IsMatch(trimmed, @"^\d+\.\s"))
                {
                    doc.InsertParagraph(trimmed)
                        .Font("Times New Roman").FontSize(14).Bold().Color(Color.Black)
                        .Alignment = Xceed.Document.NET.Alignment.center;
                    doc.InsertParagraph().SpacingAfter(5);
                }
                else
                {
                    doc.InsertParagraph(trimmed)
                        .Font("Times New Roman").FontSize(12).Color(Color.Black)
                        .SpacingBefore(0).SpacingAfter(0);
                }
            }

            if (contractData.Count == 0)
            {
                doc.InsertParagraph("Нет данных о товарах по данному контракту.")
                    .Font("Times New Roman").FontSize(12).Italic().Color(Color.Red)
                    .SpacingBefore(10).SpacingAfter(10);
            }
            else
            {
                // таблица с товарами
                var grouped = contractData.GroupBy(x => new { x.ProductName, x.Unit, x.Quantity }).ToList();
                var table = doc.AddTable(grouped.Count + 1, 5);
                table.Design = TableDesign.TableGrid;

                table.Rows[0].Cells[0].Paragraphs[0].Append("№ п/п").Bold();
                table.Rows[0].Cells[1].Paragraphs[0].Append("Наименование товара").Bold();
                table.Rows[0].Cells[2].Paragraphs[0].Append("Характеристики товара").Bold();
                table.Rows[0].Cells[3].Paragraphs[0].Append("Ед. изм.").Bold();
                table.Rows[0].Cells[4].Paragraphs[0].Append("Кол-во").Bold();

                for (int i = 0; i < grouped.Count; i++)
                {
                    var row = table.Rows[i + 1];
                    var group = grouped[i];
                    var product = group.Key;

                    row.Cells[0].Paragraphs[0].Append((i + 1).ToString());
                    row.Cells[1].Paragraphs[0].Append(product.ProductName);

                    var sb = new StringBuilder();
                    foreach (var ch in group)
                    {
                        if (!string.IsNullOrWhiteSpace(ch.Characteristic))
                            sb.AppendLine($"{ch.Characteristic}: {ch.Value}");
                    }

                    row.Cells[2].Paragraphs[0].Append(sb.ToString().Trim());
                    row.Cells[3].Paragraphs[0].Append(product.Unit);
                    row.Cells[4].Paragraphs[0].Append(product.Quantity.ToString());
                }

                doc.InsertParagraph().SpacingBefore(15);
                doc.InsertTable(table);
            }


            // 5. Подписи
            doc.InsertParagraph().SpacingBefore(30);
            var signTable = doc.AddTable(1, 2);
            signTable.SetWidths(new float[] { 300, 300 });
            signTable.Design = TableDesign.None;

            signTable.Rows[0].Cells[0].Paragraphs[0]
                .Append("ОТ ЗАКАЗЧИКА:").AppendLine()
                .Append("________________/        /").AppendLine()
                .Append("М.П.");

            signTable.Rows[0].Cells[1].Paragraphs[0]
                .Append("ОТ ПОСТАВЩИКА:").AppendLine()
                .Append("________________/        /").AppendLine()
                .Append("М.П.");

            doc.InsertParagraph().SpacingBefore(20);
            doc.InsertTable(signTable);

            using var stream = new MemoryStream();
            doc.SaveAs(stream);
            var fileBytes = stream.ToArray();

            var result = new
            {
                FileName = $"Техническое_задание_по_{dto.Law}_Контракт_{contractNumber}.docx",
                Base64 = Convert.ToBase64String(fileBytes)
            };

            return new JsonResult(result);
        }


        private string GetTechnicalText44() => @"
1. ОБЩИЕ ПОЛОЖЕНИЯ

1.1. Поставщик должен обеспечить за свой счёт, своими силами и средствами доставку товаров для освещения помещений учреждения по адресу: 142207, Московская Область, г.о. Серпухов, г Серпухов, ул Джона Рида, д. 6.

2. ТРЕБОВАНИЯ К КАЧЕСТВУ ПОСТАВЛЯЕМОГО ТОВАРА

2.1. В соответствии с п. 7 ч. 1 ст. 33 Федерального закона от 05.04.2013 N 44-ФЗ ""О контрактной системе в сфере закупок товаров, работ, услуг для обеспечения государственных и муниципальных нужд"", поставляемый товар должен быть новым (товаром, который не был в употреблении, не прошел ремонт, в том числе восстановление, замену составных частей, восстановление потребительских свойств).

2.2. Качество товара должно соответствовать требованиям ГОСТ, ТУ изготовителя, а в случае их отсутствия, аналогичным требованиям, принятым на международном уровне и, в установленных законодательством случаях. Заявленный товар должен быть произведен в заводских условиях и являться устройством заводской готовности.

2.7. Весь товар должен соответствовать требованиям, установленным в технической спецификации (раздел 6 Технического задания).

3. УСЛОВИЯ ПОСТАВКИ ТОВАРА

3.1. Упаковка товара должна обеспечить его сохранность при транспортировке и хранении.

3.2. Информация о товаре, в том числе маркировка на упаковке должна быть на русском языке или продублирована на русском языке.

3.3. Маркировка должна содержать сведения о товаре: его наименование, параметры, дату производства, номер партии, сведения о производителе товара, а также иные обозначения в соответствии с действующими международными стандартами и требованиями ГОСТ.

3.4. Принятие товара, поставленного в соответствии с условиями контракта, проверку количества, качества, ассортимента осуществляет уполномоченный представитель получателя непосредственно в момент приемки товара от поставщика с оформлением товарной накладной.

3.5. Получатель имеет право отказаться от товара, если он не соответствует требованиям, предъявляемым к качеству товара, не имеет соответствующих документов, если прилагаемые документы не соответствуют поставленной партии товара.

3.6. В случае если при приемке будет обнаружен товар ненадлежащего качества или ассортимента, заказчик обязан отказаться от приемки такого товара, известив об этом поставщика. При этом поставщик обязан заменить некачественный (дефектный) товар на качественный или соответствующий товар в течение 15 (пятнадцати) рабочих дней с момента предъявления заказчиком (получателем) такого требования. Поставщик несет все расходы, связанные с заменой некачественного (дефектного) оборудования.

4. ТРЕБОВАНИЯ К ДОКУМЕНТАЦИИ НА ТОВАР

4.1. При поставке товара поставщик передает получателю все относящиеся к товару документы (паспорт качества завода-изготовителя, сертификат качества, гарантийный талон, инструкцию по эксплуатации и т.п.).

4.2. Инструкции по эксплуатации товара и технические паспорта должны быть на русском языке либо иметь заверенный перевод на русский язык.

5. ГАРАНТИЙНОЕ ОБСЛУЖИВАНИЕ

5.1. Поставщик обязан предоставить гарантию качества на поставляемый товар. Гарантийный срок должен быть не менее 12 (Двенадцати) месяцев с момента передачи товара получателю и подписания заказчиком документов, подтверждающих исполнение обязательств в соответствии с условиями Контракта.

5.2. Поставщик в период гарантийного обслуживания товара за свой счет обязан обеспечить восстановление работоспособности товара в течение не более 15 (пятнадцати) рабочих дней с момента получения извещения от получателя о неисправности товара.

6. ТРЕБОВАНИЯ К КОЛИЧЕСТВУ (ОБЪЁМУ), ФУНКЦИОНАЛЬНЫМ И ТЕХНИЧЕСКИМ ХАРАКТЕРИСТИКАМ ТОВАРА";

        private string GetTechnicalText223() => @"
1. ОБЩИЕ ПОЛОЖЕНИЯ

1.1. Поставщик должен обеспечить за свой счёт, своими силами и средствами доставку товаров для освещения помещений учреждения по адресу: 142207, Московская Область, г.о. Серпухов, г. Серпухов, ул. Джона Рида, д. 6.

2. ТРЕБОВАНИЯ К КАЧЕСТВУ ПОСТАВЛЯЕМОГО ТОВАРА

2.1. Поставляемый товар должен быть новым (не бывшим в употреблении, не восстановленным, не отремонтированным) и соответствовать характеристикам, установленным в настоящем техническом задании.

2.2. Качество товара должно соответствовать требованиям производителя, техническим условиям, а при их отсутствии — принятым на предприятии нормам и стандартам. Товар должен быть произведён в заводских условиях и иметь необходимые документы, подтверждающие его происхождение и качество.

2.7. Весь товар должен соответствовать требованиям, установленным в технической спецификации (раздел 6 Технического задания).

3. УСЛОВИЯ ПОСТАВКИ ТОВАРА

3.1. Упаковка товара должна обеспечивать его сохранность при транспортировке, погрузке, выгрузке и хранении.

3.2. Информация о товаре и маркировка на упаковке должны быть на русском языке или сопровождаться переводом на русский язык.

3.3. Маркировка должна включать: наименование товара, его характеристики, дату производства, номер партии, сведения о производителе, а также иные сведения, предусмотренные договором поставки.

3.4. Принятие товара осуществляется уполномоченным представителем заказчика с оформлением товарной накладной или иного документа, подтверждающего факт поставки.

3.5. Заказчик имеет право отказаться от товара, если он не соответствует заявленным требованиям или не сопровождается необходимыми документами.

3.6. В случае поставки товара ненадлежащего качества или ассортимента, поставщик обязан заменить такой товар в течение 10 (десяти) рабочих дней с момента получения уведомления от заказчика. Все расходы, связанные с заменой, несёт поставщик.

4. ТРЕБОВАНИЯ К ДОКУМЕНТАЦИИ НА ТОВАР

4.1. Поставщик передаёт заказчику сопроводительную документацию: паспорт качества, сертификаты соответствия, инструкцию по эксплуатации, гарантийный талон и иные документы в соответствии с договором.

4.2. Документация должна быть на русском языке либо иметь перевод, заверенный надлежащим образом.

5. ГАРАНТИЙНОЕ ОБСЛУЖИВАНИЕ

5.1. Поставщик предоставляет гарантию качества на поставляемый товар сроком не менее 12 (двенадцати) месяцев с момента приёмки товара заказчиком.

5.2. В случае выявления неисправности в период гарантийного срока поставщик обязан за свой счёт устранить дефекты в течение 10 (десяти) рабочих дней с момента получения уведомления.

6. ТРЕБОВАНИЯ К КОЛИЧЕСТВУ (ОБЪЁМУ), ФУНКЦИОНАЛЬНЫМ И ТЕХНИЧЕСКИМ ХАРАКТЕРИСТИКАМ ТОВАРА
";

        public class ExportContractDto
        {
            public int ContractId { get; set; }
            public string Law { get; set; } = "44-ФЗ";
            public int UserId { get; set; } // Добавляем UserId в модель


        }
        public class ContractExportRow
        {
            public string ProductName { get; set; }
            public string Characteristic { get; set; }
            public string Value { get; set; }
            public string Unit { get; set; }
            public int Quantity { get; set; }
        }

        public static string ConvertAmountToWords(decimal amount)
        {
            var rub = (int)Math.Floor(amount);
            var kop = (int)((amount - rub) * 100);

            // Преобразуем сумму в слова с русской локализацией
            string rubText = rub.ToWords(new CultureInfo("ru-RU"));
            string kopText = kop.ToString("D2");

            return $"{rubText} рублей {kopText} копеек";
        }


        [HttpPost("export-contract-template")]
        public async Task<IActionResult> ExportContractTemplate([FromBody] ExportContractDto dto)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // 1. Контракт + Поставщик
            var contractQuery = @"
SELECT 
    c.contractnumber,
    c.contractdate,
    c.description,
    c.ikz,
    c.plannedamount,
    c.actualamount,
    s.name AS suppliername,
    s.shortname,
    s.inn,
    s.kpp,
    s.kpp_kn,
    s.ogrn,
    s.phone,        
    s.email,         
    s.address AS supplieraddress,
    s.postaladdress,
c.protocolnumber,
c.protocoldate
FROM contracts c
LEFT JOIN suppliers s ON c.supplierid = s.supplierid
WHERE c.contractid = @contractId;
";
            var contractCmd = new NpgsqlCommand(contractQuery, connection);
            contractCmd.Parameters.AddWithValue("@contractId", dto.ContractId);
            var reader = await contractCmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
                return NotFound("Контракт не найден");

            var contractNumber = reader.GetString(0);
            var contractDate = reader.GetDateTime(1);
            var description = reader.IsDBNull(2) ? "____________" : reader.GetString(2);
            var ikz = reader.IsDBNull(3) ? "__________________________" : reader.GetString(3);
            var plannedAmount = reader.IsDBNull(4) ? 0 : reader.GetDecimal(4);
            var actualAmount = reader.IsDBNull(5) ? 0 : reader.GetDecimal(5);
            var protocolNumber = reader.IsDBNull(16) ? "___" : reader.GetString(16);
            var protocolDate = reader.IsDBNull(17) ? DateTime.MinValue : reader.GetDateTime(17);


            var supplier = new
            {
                Name = reader.IsDBNull(6) ? "____________" : reader.GetString(6),
                ShortName = reader.IsDBNull(7) ? "____________" : reader.GetString(7),
                INN = reader.IsDBNull(8) ? "_______" : reader.GetString(8),
                KPP = reader.IsDBNull(9) ? "_______" : reader.GetString(9),
                KPP_KN = reader.IsDBNull(10) ? "_______" : reader.GetString(10),
                OGRN = reader.IsDBNull(11) ? "_______" : reader.GetString(11),
                Phone = reader.IsDBNull(12) ? "___________" : reader.GetString(12),
                Email = reader.IsDBNull(13) ? "__________" : reader.GetString(13),
                Address = reader.IsDBNull(14) ? "_____________________" : reader.GetString(14),
                PostalAddress = reader.IsDBNull(15) ? "_____________________" : reader.GetString(15),

            };




            reader.Close();

            // 2. Заказчик (ID = 1)
            var customerCmd = new NpgsqlCommand("SELECT name, inn, kpp, ogrn, address FROM customers WHERE customerid = 1;", connection);
            reader = await customerCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return NotFound("Заказчик не найден");

            var customer = new
            {
                Name = reader.GetString(0),
                INN = reader.GetString(1),
                KPP = reader.GetString(2),
                OGRN = reader.GetString(3),
                Address = reader.GetString(4)
            };
            reader.Close();

            // 3. Банковские реквизиты (ID = 1)
            var bankCmd = new NpgsqlCommand(@"
SELECT accountname, bankname, bik, personalaccount, settlementaccount,
       unifiedaccount, okpo, oktmo, okopf
FROM bankaccounts
WHERE bankaccountid = 1;", connection);
            reader = await bankCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return NotFound("Банковские реквизиты не найдены");

            var bank = new
            {
                AccountName = reader.GetString(0),
                BankName = reader.GetString(1),
                BIK = reader.GetString(2),
                PersonalAccount = reader.GetString(3),
                SettlementAccount = reader.GetString(4),
                UnifiedAccount = reader.GetString(5),
                OKPO = reader.GetString(6),
                OKTMO = reader.GetString(7),
                OKOPF = reader.GetString(8)
            };
            reader.Close();

            // 4. Продукты
            var productsQuery = @"
SELECT 
    p.name, u.unitname, ri.quantity,
    ric.valuerequest,
    ch.name
FROM requestitems ri
JOIN products p ON ri.productid = p.productid
JOIN units u ON p.unitid = u.unitid
LEFT JOIN requestitemcharacteristics ric ON ri.requestitemid = ric.requestitemid
LEFT JOIN productcharacteristics pc ON ric.productcharacteristicid = pc.productcharacteristicid
LEFT JOIN characteristics ch ON pc.characteristicid = ch.characteristicid
WHERE ri.contractid = @contractId
ORDER BY p.name;
";
            var productCmd = new NpgsqlCommand(productsQuery, connection);
            productCmd.Parameters.AddWithValue("@contractId", dto.ContractId);
            reader = await productCmd.ExecuteReaderAsync();

            var products = new List<ContractExportRow>();
            while (await reader.ReadAsync())
            {
                products.Add(new ContractExportRow
                {
                    ProductName = reader.GetString(0),
                    Unit = reader.GetString(1),
                    Quantity = reader.GetInt32(2),
                    Value = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Characteristic = reader.IsDBNull(4) ? "" : reader.GetString(4)
                });
            }
            reader.Close();

            // 5. Генерация Word
            var templateFileName = dto.Law == "223-ФЗ" ? "Умный контракт 223-ФЗ.docx" : "Умный контракт.docx";
            var templatePath = Path.Combine("Templates", templateFileName);
            var doc = DocX.Load(templatePath);

            // Статичные поля
            var deliveryPlace = "г. Серпухов, ул. Джона Рида, д. 6";
            var deliveryTime = "В течение 10 рабочих дней с даты подписания контракта";
            var sumText = actualAmount > 0 ? actualAmount.ToString("C") : plannedAmount.ToString("C");

            // Замены
            var sumValue = actualAmount > 0 ? actualAmount : plannedAmount;
            var amountWords = ConvertAmountToWords(sumValue);
            var amountNumeric = sumValue.ToString("N2") + " руб.";

            doc.ReplaceText("{AmountNumeric}", amountNumeric);
            doc.ReplaceText("{AmountWords}", amountWords);

            doc.ReplaceText("{ContractNumber}", contractNumber);
            var russianCulture = new System.Globalization.CultureInfo("ru-RU");
            var fullDateText = $"«{contractDate.ToString("dd", russianCulture)}» {contractDate.ToString("MMMM yyyy", russianCulture)} года";

            doc.ReplaceText("{ContractDate}", fullDateText);
            doc.ReplaceText("{IKZ}", ikz);
            doc.ReplaceText("{CustomerName}", customer.Name);
            doc.ReplaceText("{CustomerINN}", customer.INN);
            doc.ReplaceText("{CustomerKPP}", customer.KPP);
            doc.ReplaceText("{CustomerOGRN}", customer.OGRN);
            doc.ReplaceText("{CustomerAddress}", customer.Address);
            doc.ReplaceText("{ContractDescription}", description);
            doc.ReplaceText("{SupplierPhone}", supplier.Phone);
            doc.ReplaceText("{SupplierEmail}", supplier.Email);

            doc.ReplaceText("{SupplierFullName}", supplier.Name);
            doc.ReplaceText("{SupplierShortName}", supplier.ShortName);
            doc.ReplaceText("{SupplierAddress}", supplier.Address);
            doc.ReplaceText("{SupplierPostalAddress}", supplier.PostalAddress);
            doc.ReplaceText("{SupplierINN}", supplier.INN);
            doc.ReplaceText("{SupplierKPP}", supplier.KPP);
            doc.ReplaceText("{SupplierKPP_KN}", supplier.KPP_KN);
            doc.ReplaceText("{SupplierOGRN}", supplier.OGRN);

            doc.ReplaceText("{DeliveryPlace}", deliveryPlace);
            doc.ReplaceText("{DeliveryTime}", deliveryTime);
            doc.ReplaceText("{Amount}", sumText);

            var protocolDateFormatted = protocolDate != DateTime.MinValue
    ? $"«{protocolDate:dd}» {protocolDate:MMMM yyyy} года"
    : "____";

            doc.ReplaceText("{ProtocolNumber}", protocolNumber);
            doc.ReplaceText("{ProtocolDate}", protocolDateFormatted);


            doc.ReplaceText("{FinanceAmount}", sumValue.ToString("N2"));
            doc.ReplaceText("{FinanceYear}", contractDate.Year.ToString());

            // Банковские реквизиты
            doc.ReplaceText("{Bank_AccountName}", bank.AccountName);
            doc.ReplaceText("{Bank_BankName}", bank.BankName);
            doc.ReplaceText("{Bank_BIK}", bank.BIK);
            doc.ReplaceText("{Bank_PersonalAccount}", bank.PersonalAccount);
            doc.ReplaceText("{Bank_SettlementAccount}", bank.SettlementAccount);
            doc.ReplaceText("{Bank_UnifiedAccount}", bank.UnifiedAccount);
            doc.ReplaceText("{Bank_OKPO}", bank.OKPO);
            doc.ReplaceText("{Bank_OKTMO}", bank.OKTMO);
            doc.ReplaceText("{Bank_OKOPF}", bank.OKOPF);
            // Приведение всех параграфов к Times New Roman 12pt
            foreach (var paragraph in doc.Paragraphs)
            {
                paragraph.Font(new Font("Times New Roman")).FontSize(12);
            }



            using var ms = new MemoryStream();
            doc.SaveAs(ms);

            var fileResult = new
            {
                FileName = $"Контракт_{contractNumber}.docx",
                Base64 = Convert.ToBase64String(ms.ToArray())
            };

            return new JsonResult(fileResult);
        }

        [HttpPost("export-contract-template_ECP")]
        public async Task<IActionResult> ExportContractTemplateECP([FromBody] ExportContractDto dto)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // 1. Контракт + Поставщик
            var contractQuery = @"
SELECT 
    c.contractnumber,
    c.contractdate,
    c.description,
    c.ikz,
    c.plannedamount,
    c.actualamount,
    s.name AS suppliername,
    s.shortname,
    s.inn,
    s.kpp,
    s.kpp_kn,
    s.ogrn,
    s.phone,        
    s.email,         
    s.address AS supplieraddress,
    s.postaladdress,
c.protocolnumber,
c.protocoldate
FROM contracts c
LEFT JOIN suppliers s ON c.supplierid = s.supplierid
WHERE c.contractid = @contractId;
";
            var contractCmd = new NpgsqlCommand(contractQuery, connection);
            contractCmd.Parameters.AddWithValue("@contractId", dto.ContractId);
            var reader = await contractCmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
                return NotFound("Контракт не найден");

            var contractNumber = reader.GetString(0);
            var contractDate = reader.GetDateTime(1);
            var description = reader.IsDBNull(2) ? "____________" : reader.GetString(2);
            var ikz = reader.IsDBNull(3) ? "_________________" : reader.GetString(3);
            var plannedAmount = reader.IsDBNull(4) ? 0 : reader.GetDecimal(4);
            var actualAmount = reader.IsDBNull(5) ? 0 : reader.GetDecimal(5);
            var protocolNumber = reader.IsDBNull(16) ? "___" : reader.GetString(16);
            var protocolDate = reader.IsDBNull(17) ? DateTime.MinValue : reader.GetDateTime(17);


            var supplier = new
            {
                Name = reader.IsDBNull(6) ? "____________" : reader.GetString(6),
                ShortName = reader.IsDBNull(7) ? "____________" : reader.GetString(7),
                INN = reader.IsDBNull(8) ? "_______" : reader.GetString(8),
                KPP = reader.IsDBNull(9) ? "_______" : reader.GetString(9),
                KPP_KN = reader.IsDBNull(10) ? "_______" : reader.GetString(10),
                OGRN = reader.IsDBNull(11) ? "_______" : reader.GetString(11),
                Phone = reader.IsDBNull(12) ? "___________" : reader.GetString(12),
                Email = reader.IsDBNull(13) ? "__________" : reader.GetString(13),
                Address = reader.IsDBNull(14) ? "_____________________" : reader.GetString(14),
                PostalAddress = reader.IsDBNull(15) ? "_____________________" : reader.GetString(15),

            };




            reader.Close();

            // 2. Заказчик (ID = 1)
            var customerCmd = new NpgsqlCommand("SELECT name, inn, kpp, ogrn, address FROM customers WHERE customerid = 1;", connection);
            reader = await customerCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return NotFound("Заказчик не найден");

            var customer = new
            {
                Name = reader.GetString(0),
                INN = reader.GetString(1),
                KPP = reader.GetString(2),
                OGRN = reader.GetString(3),
                Address = reader.GetString(4)
            };
            reader.Close();

            // 3. Банковские реквизиты (ID = 1)
            var bankCmd = new NpgsqlCommand(@"
SELECT accountname, bankname, bik, personalaccount, settlementaccount,
       unifiedaccount, okpo, oktmo, okopf
FROM bankaccounts
WHERE bankaccountid = 1;", connection);
            reader = await bankCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return NotFound("Банковские реквизиты не найдены");

            var bank = new
            {
                AccountName = reader.GetString(0),
                BankName = reader.GetString(1),
                BIK = reader.GetString(2),
                PersonalAccount = reader.GetString(3),
                SettlementAccount = reader.GetString(4),
                UnifiedAccount = reader.GetString(5),
                OKPO = reader.GetString(6),
                OKTMO = reader.GetString(7),
                OKOPF = reader.GetString(8)
            };
            reader.Close();

            // 4. Продукты
            var productsQuery = @"
SELECT 
    p.name, u.unitname, ri.quantity,
    ric.valuerequest,
    ch.name
FROM requestitems ri
JOIN products p ON ri.productid = p.productid
JOIN units u ON p.unitid = u.unitid
LEFT JOIN requestitemcharacteristics ric ON ri.requestitemid = ric.requestitemid
LEFT JOIN productcharacteristics pc ON ric.productcharacteristicid = pc.productcharacteristicid
LEFT JOIN characteristics ch ON pc.characteristicid = ch.characteristicid
WHERE ri.contractid = @contractId
ORDER BY p.name;
";
            var productCmd = new NpgsqlCommand(productsQuery, connection);
            productCmd.Parameters.AddWithValue("@contractId", dto.ContractId);
            reader = await productCmd.ExecuteReaderAsync();

            var products = new List<ContractExportRow>();
            while (await reader.ReadAsync())
            {
                products.Add(new ContractExportRow
                {
                    ProductName = reader.GetString(0),
                    Unit = reader.GetString(1),
                    Quantity = reader.GetInt32(2),
                    Value = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Characteristic = reader.IsDBNull(4) ? "" : reader.GetString(4)
                });
            }
            reader.Close();

            // 5. Генерация Word
            var templateFileName = dto.Law == "223-ФЗ" ? "Умный контракт 223-ФЗ.docx" : "Умный контракт.docx";
            var templatePath = Path.Combine("Templates", templateFileName);
            var doc = DocX.Load(templatePath);

            // Статичные поля
            var deliveryPlace = "г. Серпухов, ул. Джона Рида, д. 6";
            var deliveryTime = "В течение 10 рабочих дней с даты подписания контракта";
            var sumText = actualAmount > 0 ? actualAmount.ToString("C") : plannedAmount.ToString("C");

            // Замены
            var sumValue = actualAmount > 0 ? actualAmount : plannedAmount;
            var amountWords = ConvertAmountToWords(sumValue);
            var amountNumeric = sumValue.ToString("N2") + " руб.";

            doc.ReplaceText("{AmountNumeric}", amountNumeric);
            doc.ReplaceText("{AmountWords}", amountWords);

            doc.ReplaceText("{ContractNumber}", contractNumber);
            var russianCulture = new System.Globalization.CultureInfo("ru-RU");
            var fullDateText = $"«{contractDate.ToString("dd", russianCulture)}» {contractDate.ToString("MMMM yyyy", russianCulture)} года";

            doc.ReplaceText("{ContractDate}", fullDateText);
            doc.ReplaceText("{IKZ}", ikz);
            doc.ReplaceText("{CustomerName}", customer.Name);
            doc.ReplaceText("{CustomerINN}", customer.INN);
            doc.ReplaceText("{CustomerKPP}", customer.KPP);
            doc.ReplaceText("{CustomerOGRN}", customer.OGRN);
            doc.ReplaceText("{CustomerAddress}", customer.Address);
            doc.ReplaceText("{ContractDescription}", description);
            doc.ReplaceText("{SupplierPhone}", supplier.Phone);
            doc.ReplaceText("{SupplierEmail}", supplier.Email);

            doc.ReplaceText("{SupplierFullName}", supplier.Name);
            doc.ReplaceText("{SupplierShortName}", supplier.ShortName);
            doc.ReplaceText("{SupplierAddress}", supplier.Address);
            doc.ReplaceText("{SupplierPostalAddress}", supplier.PostalAddress);
            doc.ReplaceText("{SupplierINN}", supplier.INN);
            doc.ReplaceText("{SupplierKPP}", supplier.KPP);
            doc.ReplaceText("{SupplierKPP_KN}", supplier.KPP_KN);
            doc.ReplaceText("{SupplierOGRN}", supplier.OGRN);

            doc.ReplaceText("{DeliveryPlace}", deliveryPlace);
            doc.ReplaceText("{DeliveryTime}", deliveryTime);
            doc.ReplaceText("{Amount}", sumText);

            var protocolDateFormatted = protocolDate != DateTime.MinValue
    ? $"«{protocolDate:dd}» {protocolDate:MMMM yyyy} года"
    : "____";

            doc.ReplaceText("{ProtocolNumber}", protocolNumber);
            doc.ReplaceText("{ProtocolDate}", protocolDateFormatted);


            doc.ReplaceText("{FinanceAmount}", sumValue.ToString("N2"));
            doc.ReplaceText("{FinanceYear}", contractDate.Year.ToString());

            // Банковские реквизиты
            doc.ReplaceText("{Bank_AccountName}", bank.AccountName);
            doc.ReplaceText("{Bank_BankName}", bank.BankName);
            doc.ReplaceText("{Bank_BIK}", bank.BIK);
            doc.ReplaceText("{Bank_PersonalAccount}", bank.PersonalAccount);
            doc.ReplaceText("{Bank_SettlementAccount}", bank.SettlementAccount);
            doc.ReplaceText("{Bank_UnifiedAccount}", bank.UnifiedAccount);
            doc.ReplaceText("{Bank_OKPO}", bank.OKPO);
            doc.ReplaceText("{Bank_OKTMO}", bank.OKTMO);
            doc.ReplaceText("{Bank_OKOPF}", bank.OKOPF);
            // Приведение всех параграфов к Times New Roman 12pt
            foreach (var paragraph in doc.Paragraphs)
            {
                paragraph.Font(new Font("Times New Roman")).FontSize(12);
            }



            // Сохраняем документ в MemoryStream
            using var ms = new MemoryStream();
            doc.SaveAs(ms);
            ms.Position = 0;
            byte[] documentBytes = ms.ToArray();

            // 6. Получение сертификата пользователя
            var certificateQuery = @"
    SELECT uc.CertificateData, uc.CertificateType, uk.PrivateKeyData, uk.KeyPassword
    FROM Users u
    LEFT JOIN UserCertificates uc ON u.certificate_id = uc.UserCertificateID
    LEFT JOIN UserPrivateKeys uk ON u.private_key_id = uk.UserPrivateKeyID
    WHERE u.UserID = @userId";

            byte[] certificateData = null;
            byte[] privateKeyData = null;
            string keyPassword = null;
            string certificateType = null;

            using (var cmd = new NpgsqlCommand(certificateQuery, connection))
            {
                cmd.Parameters.AddWithValue("@userId", dto.UserId);

                using (var certReader = await cmd.ExecuteReaderAsync()) // Изменили имя переменной на certReader
                {
                    if (await certReader.ReadAsync())
                    {
                        if (!certReader.IsDBNull(0)) certificateData = certReader.GetFieldValue<byte[]>(0);
                        if (!certReader.IsDBNull(1)) certificateType = certReader.GetString(1);
                        if (!certReader.IsDBNull(2)) privateKeyData = certReader.GetFieldValue<byte[]>(2);
                        if (!certReader.IsDBNull(3)) keyPassword = certReader.GetString(3);
                    }
                }
            }

            // Если сертификата нет - генерируем новый
            if (certificateData == null || privateKeyData == null)
            {
                var generateResult = await GenerateUserCertificate(dto.UserId);
                if (!generateResult.Success)
                {
                    return BadRequest("Не удалось сгенерировать сертификат: " + generateResult.Message);
                }

                // Повторно запрашиваем данные сертификата
                // Повторно запрашиваем данные сертификата
                using (var cmd = new NpgsqlCommand(certificateQuery, connection))
                {
                    cmd.Parameters.AddWithValue("@userId", dto.UserId);

                    // Изменяем имя переменной с reader на certReader
                    using (var certReader = await cmd.ExecuteReaderAsync())
                    {
                        if (await certReader.ReadAsync())
                        {
                            certificateData = certReader.GetFieldValue<byte[]>(0);
                            certificateType = certReader.GetString(1);
                            privateKeyData = certReader.GetFieldValue<byte[]>(2);
                            keyPassword = certReader.GetString(3);
                        }
                    }
                }
            }
            if (certificateData == null || privateKeyData == null)
            {
                return BadRequest("У пользователя нет сертификата или приватного ключа");
            }

            // Загрузка сертификата и ключа
            X509Certificate2 certificate;
            try
            {
                if (certificateType == "PFX")
                {
                    certificate = new X509Certificate2(
                        certificateData,
                        keyPassword,
                        X509KeyStorageFlags.Exportable |
                        X509KeyStorageFlags.PersistKeySet |
                        X509KeyStorageFlags.UserKeySet
                    );
                }
                else
                {
                    certificate = new X509Certificate2(certificateData);
                    using var rsa = RSA.Create();
                    rsa.ImportEncryptedPkcs8PrivateKey(keyPassword, privateKeyData, out _);
                    certificate = certificate.CopyWithPrivateKey(rsa);
                }

                if (!certificate.HasPrivateKey)
                {
                    return BadRequest("Сертификат не содержит закрытый ключ.");
                }

                // Проверка срока действия
                if (certificate.NotBefore > DateTime.Now || certificate.NotAfter < DateTime.Now)
                {
                    return BadRequest("Сертификат недействителен. Проверьте срок действия.");
                }
            }
            catch (Exception ex)
            {
                return BadRequest($"Ошибка загрузки сертификата: {ex.Message}");
            }
            var privateKey = certificate.GetRSAPrivateKey();
            if (privateKey == null)
            {
                return BadRequest("Не удалось получить приватный ключ");
            }

            // Подписание документа
            byte[] signedData;
            try
            {
                signedData = SignDocumentWithPKCS7(documentBytes, privateKey, certificate);

                // Проверка подписи
                if (!ValidateDigitalSignature(signedData, dto.UserId))
                {
                    return BadRequest("Недействительная подпись. Документ не может быть подписан.");
                }
            }
            catch (Exception ex)
            {
                return BadRequest($"Ошибка подписания документа: {ex.Message}");
            }

            byte[] tsBytes = BuildTechnicalSpecification(
       contractNumber,
       contractDate,
       dto.Law,
       products);

            // 5.2 Подписание ТЗ
            byte[] tsSigned = SignDocumentWithPKCS7(tsBytes, privateKey, certificate);
            if (!ValidateDigitalSignature(tsSigned, dto.UserId))
                return BadRequest("Недействительная подпись ТЗ");

            // --- 6. Создание ZIP архива для скачивания ---
            using (var zipMemoryStream = new MemoryStream())
            {
                using (var archive = new ZipArchive(zipMemoryStream, ZipArchiveMode.Create, true))
                {
                    // 6.1 Добавление подписанного договора
                    var docEntry = archive.CreateEntry($"Контракт_{contractNumber}.docx");
                    using (var docStream = docEntry.Open())
                        docStream.Write(documentBytes, 0, documentBytes.Length);

                    var signatureEntry = archive.CreateEntry($"Контракт_{contractNumber}.p7s");
                    using (var sigStream = signatureEntry.Open())
                        sigStream.Write(signedData, 0, signedData.Length);

                    // 6.2 Добавление подписанного ТЗ
                    var tsDocEntry = archive.CreateEntry($"ТЗ_{contractNumber}.docx");
                    using (var tsDocStream = tsDocEntry.Open())
                        tsDocStream.Write(tsBytes, 0, tsBytes.Length);

                    var tsSigEntry = archive.CreateEntry($"ТЗ_{contractNumber}.p7s");
                    using (var tsSigStream = tsSigEntry.Open())
                        tsSigStream.Write(tsSigned, 0, tsSigned.Length);

                    // 6.3 Условия контракта (HTML) — по желанию
                    var conditionsEntry = archive.CreateEntry($"Условия_контракта_{contractNumber}.html");
                    using (var conditionsStream = new StreamWriter(conditionsEntry.Open(), Encoding.UTF8))
                    {
                        var fileNames = new List<string>
{
    $"Контракт_{contractNumber}.docx",
    $"Контракт_{contractNumber}.p7s",
    $"ТЗ_{contractNumber}.docx",
    $"ТЗ_{contractNumber}.p7s",
    $"Условия_контракта_{contractNumber}.html"
};
                        int currentUserId = dto.UserId;

                        var htmlContent = GenerateContractConditionsHtml(
        contractNumber,
        contractDate,
        description,
        customer,
        supplier,
        protocolNumber,
        protocolDate,
        sumValue,
        fileNames,
        currentUserId
        ); 
                        conditionsStream.Write(htmlContent);
                    }
                }

                return File(zipMemoryStream.ToArray(), "application/zip", $"Контракт_{contractNumber}_с_ТЗ_подписан.zip");
            }
        }

        /// <summary>
        /// Строит «Техническое задание» в виде byte[]
        /// </summary>
        private byte[] BuildTechnicalSpecification(
        string contractNumber,
        DateTime contractDate,
        string law,
        List<ContractExportRow> products)
        {
            using var ms = new MemoryStream();
            var doc = DocX.Create(ms);

            // 1. Заголовок
            doc.InsertParagraph($"Приложение № 5")
               .Alignment = Xceed.Document.NET.Alignment.right;
            doc.InsertParagraph($"к Контракту № {contractNumber}")
               .Alignment = Xceed.Document.NET.Alignment.right;
            doc.InsertParagraph($"от «{contractDate:dd}» {contractDate:MMMM} {contractDate:yyyy} г.")
               .Alignment = Xceed.Document.NET.Alignment.right;
            doc.InsertParagraph().SpacingAfter(10);

            // 2. Название раздела
            doc.InsertParagraph("ТЕХНИЧЕСКОЕ ЗАДАНИЕ")
               .Font("Times New Roman").FontSize(16).Bold()
               .Alignment = Xceed.Document.NET.Alignment.center;
            doc.InsertParagraph().SpacingAfter(10);

            // 3. Текст ТЗ
            string technicalText = law == "223-ФЗ" ? GetTechnicalText223() : GetTechnicalText44();
            foreach (var paragraph in technicalText.Split('\n'))
            {
                var trimmed = paragraph.Trim();
                if (Regex.IsMatch(trimmed, @"^\d+\.\s"))
                {
                    doc.InsertParagraph(trimmed)
                       .Font("Times New Roman").FontSize(14).Bold()
                       .Alignment = Xceed.Document.NET.Alignment.left;
                      
                }
                else
                {
                    doc.InsertParagraph(trimmed)
                       .Font("Times New Roman").FontSize(12)
                       .Alignment = Xceed.Document.NET.Alignment.left;
                    
                }
            }

            // 4. Таблица товаров
            if (products != null && products.Count > 0)
            {
                doc.InsertParagraph().SpacingBefore(15);

                var grouped = products
                    .GroupBy(x => new { x.ProductName, x.Unit, x.Quantity })
                    .ToList();

                // создаём таблицу: (N+1) строк и 5 столбцов
                var table = doc.AddTable(grouped.Count + 1, 5);
                table.Design = TableDesign.TableGrid;

                // заголовки
                string[] headers = { "№ п/п", "Наименование товара", "Характеристики товара", "Ед. изм.", "Кол-во" };
                for (int c = 0; c < headers.Length; c++)
                {
                    table.Rows[0].Cells[c].Paragraphs[0]
                         .Append(headers[c])
                         .Bold()
                         .Font("Times New Roman").FontSize(12);
                }

                // строки с данными
                for (int i = 0; i < grouped.Count; i++)
                {
                    var row = table.Rows[i + 1];
                    var key = grouped[i].Key;

                    row.Cells[0].Paragraphs[0].Append((i + 1).ToString());
                    row.Cells[1].Paragraphs[0].Append(key.ProductName);

                    // собираем характеристики
                    var sb = new StringBuilder();
                    foreach (var item in grouped[i])
                    {
                        if (!string.IsNullOrWhiteSpace(item.Characteristic))
                            sb.AppendLine($"{item.Characteristic}: {item.Value}");
                    }
                    row.Cells[2].Paragraphs[0].Append(sb.ToString().TrimEnd());

                    row.Cells[3].Paragraphs[0].Append(key.Unit);
                    row.Cells[4].Paragraphs[0].Append(key.Quantity.ToString());

                    // шрифт и размер
                    for (int c = 0; c < 5; c++)
                    {
                        row.Cells[c].Paragraphs[0]
                            .Font("Times New Roman").FontSize(12);
                    }
                }

                doc.InsertTable(table);
            }
            else
            {
                doc.InsertParagraph("Нет данных о товарах по данному контракту.")
                   .Font("Times New Roman").FontSize(12).Italic()
                   .Color(Color.Red)
                   .SpacingBefore(10).SpacingAfter(10);
            }

            // 5. Блок подписей
            doc.InsertParagraph().SpacingBefore(20);
            var signTable = doc.AddTable(1, 2);
            signTable.Design = TableDesign.None;
            signTable.SetWidths(new float[] { 300f, 300f });

            signTable.Rows[0].Cells[0].Paragraphs[0]
                .Append("ОТ ЗАКАЗЧИКА:").Bold().AppendLine()
                .Append("________________/        /").AppendLine()
                .Append("М.П.")
                .Font("Times New Roman").FontSize(12);

            signTable.Rows[0].Cells[1].Paragraphs[0]
                .Append("ОТ ПОСТАВЩИКА:").Bold().AppendLine()
                .Append("________________/        /").AppendLine()
                .Append("М.П.")
                .Font("Times New Roman").FontSize(12);

            doc.InsertTable(signTable);

            // 6. Сохранение
            doc.SaveAs(ms);
            return ms.ToArray();
        }

        // 1) Метод для получения последнего сертификата пользователя из БД
        private X509Certificate2? LoadLatestCertificate(int userId, out DateTime? expiryDate)
        {
            expiryDate = null;

            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();

            // 1. Берём из UserCertificates + KeyPassword из UserPrivateKeys
            var sql = @"
SELECT 
    uc.CertificateData, 
    uc.ExpiryDate,
    uk.KeyPassword
FROM UserCertificates uc
LEFT JOIN UserPrivateKeys uk 
    ON uc.UserID = uk.UserID
WHERE uc.UserID = @userId
ORDER BY uc.DateIssued DESC
LIMIT 1;
";
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@userId", userId);
            using var reader = cmd.ExecuteReader();

            if (!reader.Read())
                return null;

            var raw = (byte[])reader["CertificateData"];
            expiryDate = reader.IsDBNull(reader.GetOrdinal("ExpiryDate"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("ExpiryDate"));
            var password = reader.IsDBNull(reader.GetOrdinal("KeyPassword"))
                ? null
                : reader.GetString(reader.GetOrdinal("KeyPassword"));

            try
            {
                // Создаём X509Certificate2, передавая пароль
                return new X509Certificate2(
                    raw,
                    password,
                    X509KeyStorageFlags.Exportable |
                    X509KeyStorageFlags.PersistKeySet |
                    X509KeyStorageFlags.UserKeySet
                );
            }
            catch (CryptographicException ex)
            {
                // Логируем и пробрасываем дальше
                throw new CryptographicException("Не удалось распаковать PFX — неверный пароль.", ex);
            }
        }

        private string GenerateContractConditionsHtml(
      string contractNumber,
      DateTime contractDate,
      string description,
      dynamic customer,
      dynamic supplier,
      string protocolNumber,
      DateTime protocolDate,
      decimal amount,
    IEnumerable<string> fileNames,
    int userId)
        {
            // Экранируем имена для HTML
            var filesHtml = string.Join("<br/>",
                fileNames.Select(fn => System.Net.WebUtility.HtmlEncode(fn)));

            // 2. Подгружаем сертификат
            var cert = LoadLatestCertificate(userId, out var expiryDate);

            // 3. Извлекаем серийник и даты из реального сертификата
            var serialNumber = cert?.SerialNumber ?? "—";
            // NotBefore берём прямо из объекта, а NotAfter — из поля expiryDate или cert.NotAfter
            var validFrom = cert != null ? cert.NotBefore.ToString("dd.MM.yyyy") : "—";
            var validTo = expiryDate.HasValue
                           ? expiryDate.Value.ToString("dd.MM.yyyy")
                           : (cert != null ? cert.NotAfter.ToString("dd.MM.yyyy") : "—");
            return $@"
<!DOCTYPE html>
<html lang='ru'>
<head>
    <meta charset='UTF-8'>
    <title>Лист подписания условий контракта</title>
    <style>
    body {{
        font-family: Arial, sans-serif;
        margin: 20px;
        line-height: 1.5;
    }}
    h1, h2 {{
        font-size: 14pt;
        margin-bottom: 10px;
    }}
    .document-info {{
        margin-bottom: 15px;
    }}
    .document-info div {{
        margin-bottom: 5px;
    }}
    .section {{
        margin-bottom: 15px;
    }}
    .section-title {{
        font-weight: bold;
        margin-bottom: 5px;
    }}
    table {{
        border-collapse: collapse;
        width: 100%;
        margin-bottom: 15px;
        font-size: 12pt;
    }}
    th, td {{
        border: 1px solid #000;
        padding: 8px;
        vertical-align: top;
    }}
    .signature-table {{
        width: 100%;
    }}
    .signature-label {{
        width: 30%;
        font-weight: bold;
    }}
    .footer {{
        margin-top: 20px;
        font-size: 11pt;
    }}
    .bold {{
        font-weight: bold;
    }}

    /* === Начало новых правил для блока подписи === */

    .signature-block {{
        border: 1px solid #000;
        border-radius: 10px;
        padding: 8px;
        margin-top: 10px;
        overflow: hidden; /* чтобы скругления применялись */
    }}

    .signature-table {{
        width: 100%;
        border-collapse: collapse;
        font-size: 12pt;
    }}

    .signature-table td {{
        border: none; /* убираем внутренние рамки */
        padding: 6px 8px;
        vertical-align: top;
    }}

    /* внешняя рамка таблицы */
    .signature-block .signature-table {{
        border: 1px solid #000;
    }}

    /* зебро-расцветка строк */
    .signature-table tr:nth-child(even) {{
        background-color: #e0e0e0;
    }}

    /* утолщённые линии между блоками */
    .signature-table tr:nth-child(2) td {{
        border-top: 2px solid #000;
    }}
    .signature-table tr:nth-child(3) td {{
        border-top: 2px solid #000;
    }}

    /* === Конец новых правил === */
</style>

</head>
<body>
    <h1>Лист подписания условий контракта</h1>

    <div class='section'>
        <h2>Сведения о документе</h2>
        <div class='document-info'>
            <div>Наименование документа: Условия контракта</div>
            <div>Документ от: <span class='bold'>{contractDate:dd.MM.yyyy} (МСК)</span></div>
            <div>Предмет контракта: {description}</div>
        </div>
    </div>

    <div class='section'>
        <h2>Сведения о заказчике</h2>
        <div class='document-info'>
            <div>Наименование: МУНИЦИПАЛЬНОЕ БЮДЖЕТНОЕ ОБЩЕОБРАЗОВАТЕЛЬНОЕ УЧРЕЖДЕНИЕ ""ОБРАЗОВАТЕЛЬНЫЙ КОМПЛЕКС ИМ. ВЛАДИМИРА ХРАБРОГО""</div>
            <div>ИНН: 5043087076</div>
            <div>КПП: 504301001</div>
        </div>
    </div>

    <div class='section'>
        <h2>Сведения о подписании документа</h2>
        <div class='document-info'>
            <div><span class='bold'>От заказчика:</span> МУНИЦИПАЛЬНОЕ БЮДЖЕТНОЕ ОБЩЕОБРАЗОВАТЕЛЬНОЕ УЧРЕЖДЕНИЕ ""ОБРАЗОВАТЕЛЬНЫЙ КОМПЛЕКС ИМ. ВЛАДИМИРА ХРАБРОГО""</div>
        </div>
    </div>


 <div class='signature-block'>
    <table class='signature-table'>
            <!-- Файлы -->
             <tr>
        <td class='bold' style='width:35%;'>
          Наименование файла(ов):
        </td>
        <td>
          {filesHtml}
        </td>
      </tr>
            <!-- Решение -->
            <tr>
                <td class='bold'>Решение:</td>
                <td>Подписан</td>
            </tr>
            <!-- Владелец -->
            <tr>
                <td class='bold'>Владелец:</td>
                <td>Ненашева Олеся Александровна*</td>
            </tr>
            <!-- Должность -->
            <tr>
                <td class='bold'>Должность:</td>
                <td>Директор</td>
            </tr>
            <!-- Дата подписания -->
            <tr>
                <td class='bold'>Дата подписания:</td>
                <td>{DateTime.Now:dd.MM.yyyy HH:mm} (МСК) (UTC+03:00)</td>
            </tr>
            <!-- Сертификат -->
            <tr>
                <td class='bold'>Серийный номер сертификата:</td>
        <td>{serialNumber}</td>
            </tr>
            <!-- Срок действия -->
            <tr>
                <td class='bold'>Срок действия:</td>
        <td>с {validFrom} (МСК) по {validTo} (МСК)</td>
            </tr>
        
        </table>
   </div>

</body>
</html>";
        }

        private string GenerateRandomId(int length)
        {
            var random = new Random();
            const string chars = "0123456789";
            return new string(Enumerable.Repeat(chars, length)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }

        private string GenerateRandomHexId(int length)
        {
            var random = new Random();
            const string chars = "0123456789ABCDEF";
            return new string(Enumerable.Repeat(chars, length)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }

        private async Task<(bool Success, string Message)> GenerateUserCertificate(int userId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            try
            {
                // Получаем данные пользователя
                var userQuery = "SELECT FirstName, MiddleName, LastName, Email FROM Users WHERE UserID = @userId";
                string firstName = "", lastName = "", email = "";

                await using (var cmd = new NpgsqlCommand(userQuery, connection))
                {
                    cmd.Parameters.AddWithValue("@userId", userId);
                    await using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        firstName = reader.IsDBNull(0) ? "" : reader.GetString(0);
                        lastName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        email = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    }
                }

                // Генерация сертификата
                var subject = new X500DistinguishedName($"CN={userId}, OU={lastName} {firstName}, O=Organization, E={email}");

                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest(
                    subject,
                    rsa,
                    System.Security.Cryptography.HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                request.CertificateExtensions.Add(
              new X509KeyUsageExtension(
                  X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation,
                  critical: true));

                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(
                        certificateAuthority: false,
                        hasPathLengthConstraint: false,
                        pathLengthConstraint: 0,
                        critical: true));


                var notBefore = DateTimeOffset.UtcNow;
                var notAfter = notBefore.AddYears(2);

                using var certificate = request.CreateSelfSigned(notBefore, notAfter);
                var password = Guid.NewGuid().ToString();
                var pfxBytes = certificate.Export(X509ContentType.Pfx, password);

                // Сохранение в транзакции
                await using var transaction = await connection.BeginTransactionAsync();

                try
                {
                    // Сохраняем приватный ключ
                    var privateKeyQuery = @"
                INSERT INTO UserPrivateKeys (UserID, PrivateKeyData, KeyPassword)
                VALUES (@userId, @privateKey, @password)
                RETURNING UserPrivateKeyID";

                    await using (var cmd = new NpgsqlCommand(privateKeyQuery, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@userId", userId);
                        cmd.Parameters.AddWithValue("@privateKey",
                            rsa.ExportEncryptedPkcs8PrivateKey(
                                password,
                                new PbeParameters(
                                    PbeEncryptionAlgorithm.Aes256Cbc,
                                    System.Security.Cryptography.HashAlgorithmName.SHA256,
                                    100000)));
                        cmd.Parameters.AddWithValue("@password", password);

                        var privateKeyId = (int)(await cmd.ExecuteScalarAsync())!;

                        // Сохраняем сертификат
                        var certQuery = @"
                    INSERT INTO UserCertificates (UserID, CertificateData, CertificateType, ExpiryDate)
                    VALUES (@userId, @certData, 'PFX', @expiry)
                    RETURNING UserCertificateID";

                        await using (var certCmd = new NpgsqlCommand(certQuery, connection, transaction))
                        {
                            certCmd.Parameters.AddWithValue("@userId", userId);
                            certCmd.Parameters.AddWithValue("@certData", pfxBytes);
                            certCmd.Parameters.AddWithValue("@expiry", notAfter.DateTime);

                            var certId = (int)(await certCmd.ExecuteScalarAsync())!;

                            // Обновляем пользователя
                            var updateUserQuery = @"
                        UPDATE Users 
                        SET certificate_id = @certId, private_key_id = @keyId
                        WHERE UserID = @userId";

                            await using (var updateCmd = new NpgsqlCommand(updateUserQuery, connection, transaction))
                            {
                                updateCmd.Parameters.AddWithValue("@certId", certId);
                                updateCmd.Parameters.AddWithValue("@keyId", privateKeyId);
                                updateCmd.Parameters.AddWithValue("@userId", userId);

                                await updateCmd.ExecuteNonQueryAsync();
                            }
                        }
                    }

                    await transaction.CommitAsync();
                    return (true, "Сертификат успешно создан");
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    return (false, $"Ошибка при создании сертификата: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Ошибка генерации сертификата: {ex.Message}");
            }
        }

        private bool ValidateDigitalSignature(byte[] signedData, int expectedUserId)
        {
            try
            {
                var signedCms = new SignedCms();
                signedCms.Decode(signedData);

                signedCms.CheckSignature(verifySignatureOnly: true);

                foreach (SignerInfo signer in signedCms.SignerInfos)
                {
                    var cert = signer.Certificate;

                    // Проверка срока действия
                    if (cert.NotBefore > DateTime.Now || cert.NotAfter < DateTime.Now)
                    {
                        throw new Exception($"Сертификат недействителен. Срок: {cert.NotBefore} - {cert.NotAfter}");
                    }

                    // Проверка алгоритма
                    if (cert.SignatureAlgorithm.Value != "1.2.840.113549.1.1.11")
                    {
                        throw new Exception($"Неподдерживаемый алгоритм: {cert.SignatureAlgorithm.FriendlyName}");
                    }

                    // Проверка, что сертификат принадлежит ожидаемому пользователю
                    var userId = GetUserIdFromCertificate(cert);
                    if (userId != expectedUserId)
                    {
                        throw new Exception($"Сертификат принадлежит другому пользователю");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка валидации: {ex.Message}");
                return false;
            }
        }

        private int GetUserIdFromCertificate(X509Certificate2 cert)
        {
            // Реализация зависит от того, как вы храните UserID в сертификате
            // Например, можно использовать Subject или Custom OID
            var subject = cert.Subject;

            // Пример: Subject содержит UserID в формате "CN=12345, OU=Users,..."
            var cnMatch = Regex.Match(subject, @"CN=(\d+)");
            if (cnMatch.Success && int.TryParse(cnMatch.Groups[1].Value, out var userId))
            {
                return userId;
            }

            // Альтернативно: можно сделать запрос в базу данных для сопоставления
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            var query = "SELECT UserID FROM UserCertificates WHERE CertificateThumbprint = @thumbprint";
            using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@thumbprint", cert.Thumbprint);

            var result = cmd.ExecuteScalar();
            return result != null ? Convert.ToInt32(result) : -1;
        }

        private byte[] SignDocumentWithPKCS7(byte[] documentBytes, RSA privateKey, X509Certificate2 certificate)
        {
            try
            {
                var contentInfo = new ContentInfo(documentBytes);
                var signedCms = new SignedCms(contentInfo, detached: false);

                var cmsSigner = new CmsSigner(certificate)
                {
                    IncludeOption = X509IncludeOption.WholeChain,
                    DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") // SHA256
                };

                signedCms.ComputeSignature(cmsSigner);
                return signedCms.Encode();
            }
            catch (Exception ex)
            {
                throw;
            }
        }








        [HttpPost("manual-contract")]
        public async Task<IActionResult> AddManualContract([FromBody] ManualContractDto dto)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = await connection.BeginTransactionAsync();

            try
            {
                // Подсчет суммы
                decimal plannedAmount = dto.Items.Sum(i => (i.UnitPrice ?? 0) * i.Quantity);

                byte[]? photoBytes = null;

                if (!string.IsNullOrWhiteSpace(dto.ManualRequestPhotoBase64))
                {
                    try
                    {
                        photoBytes = Convert.FromBase64String(dto.ManualRequestPhotoBase64);
                    }
                    catch
                    {
                        return BadRequest("Некорректный формат изображения. Проверьте Base64.");
                    }
                }


                // Вставка контракта
                var insertContractQuery = @"
INSERT INTO contracts (
    statusid, bankaccountsid, contractnumber, plannedamount, actualamount, contactphone,
    manual_request_photo, description, ikz, protocolnumber, protocoldate
)
VALUES (
    @StatusId, 1, @ContractNumber, @PlannedAmount, NULL, @ContactPhone,
    @ManualRequestPhoto, @Description, @IKZ, @ProtocolNumber, @ProtocolDate
)
RETURNING contractid;";


                int contractId;
                using (var cmd = new NpgsqlCommand(insertContractQuery, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@StatusId", dto.StatusId);
                    cmd.Parameters.AddWithValue("@ContractNumber", dto.ContractNumber);
                    cmd.Parameters.AddWithValue("@PlannedAmount", plannedAmount);
                    cmd.Parameters.AddWithValue("@ContactPhone", (object?)dto.ContactPhone ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ManualRequestPhoto", (object?)photoBytes ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Description", (object?)dto.Description ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@IKZ", (object?)dto.IKZ ?? DBNull.Value); // 🔹 Новый параметр
                    cmd.Parameters.AddWithValue("@ProtocolNumber", (object?)dto.ProtocolNumber ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ProtocolDate", (object?)dto.ProtocolDate ?? DBNull.Value);


                    contractId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }


                // Вставка товаров
                foreach (var item in dto.Items)
                {
                    var insertItemQuery = @"
                INSERT INTO requestitems (contractid, productid, quantity, unitprice, totalprice)
                VALUES (@ContractId, @ProductID, @Quantity, @UnitPrice, @TotalPrice)
                RETURNING requestitemid;";

                    int requestItemId;
                    using (var cmd = new NpgsqlCommand(insertItemQuery, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@ContractId", contractId);
                        cmd.Parameters.AddWithValue("@ProductID", item.ProductID);
                        cmd.Parameters.AddWithValue("@Quantity", item.Quantity);
                        cmd.Parameters.AddWithValue("@UnitPrice", (object?)item.UnitPrice ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@TotalPrice", (item.UnitPrice ?? 0) * item.Quantity);

                        requestItemId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                    }

                    // Вставка характеристик
                    foreach (var ch in item.Characteristics)
                    {
                        var insertCharQuery = @"
                    INSERT INTO requestitemcharacteristics (requestitemid, productcharacteristicid, valuerequest)
                    VALUES (@RequestItemID, @ProductCharacteristicID, @ValueRequest);";

                        using var cmd = new NpgsqlCommand(insertCharQuery, connection, transaction);
                        cmd.Parameters.AddWithValue("@RequestItemID", requestItemId);
                        cmd.Parameters.AddWithValue("@ProductCharacteristicID", ch.ProductCharacteristicID);
                        cmd.Parameters.AddWithValue("@ValueRequest", ch.ValueRequest);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                await transaction.CommitAsync();
                return Ok(new { ContractId = contractId });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, $"Ошибка при сохранении контракта: {ex.Message}");
            }
        }


        public class ManualContractDto
        {
            public string ContractNumber { get; set; }
            public int StatusId { get; set; }
            public string? ContactPhone { get; set; }
            public List<ManualContractItemDto> Items { get; set; }

            public string? ManualRequestPhotoBase64 { get; set; } // ❗ Новое поле
            public string? Description { get; set; } // 👈 Новое поле
            public string? IKZ { get; set; } // 🔹 Добавлено для поддержки ИКЗ

            public string? ProtocolNumber { get; set; }      // ← новое поле
            public DateTime? ProtocolDate { get; set; }

        }

        public class ManualContractItemDto
        {
            public int ProductID { get; set; }
            public int Quantity { get; set; }
            public decimal? UnitPrice { get; set; }
            public List<ManualContractCharacteristicDto> Characteristics { get; set; }

        }

        public class ManualContractCharacteristicDto
        {
            public int ProductCharacteristicID { get; set; }
            public string ValueRequest { get; set; }
        }


        [HttpGet("details/{id}")]
        public async Task<IActionResult> GetContractDetails(int id)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Запрос для контракта
            var contractQuery = @"
        SELECT 
            c.contractid,
            c.contractnumber,
            c.contractdate,
            c.statusid,
            s.name AS statusname,
            c.contactphone,
            c.description,
            c.manual_request_photo,
            COALESCE(c.actualamount, 0),
            c.requestid,
            c.supplierid,
            c.ikz,
    c.protocolnumber,  
    c.protocoldate    
        FROM contracts c
        JOIN statuses_contract s ON c.statusid = s.statusid
        WHERE c.contractid = @ContractId;
    ";

            // Запрос для товаров в контракте
            var itemQuery = @"
        SELECT 
            ri.requestitemid,
            ri.productid,
            p.name AS productname,
            u.unitid,
            u.unitname,
            ri.quantity,
            ri.unitprice,
            ric.productcharacteristicid,
            ric.valuerequest,
            ch.characteristicid,
            ch.name AS characteristicname
        FROM requestitems ri
        JOIN products p ON ri.productid = p.productid
        JOIN units u ON p.unitid = u.unitid
        LEFT JOIN requestitemcharacteristics ric ON ri.requestitemid = ric.requestitemid
        LEFT JOIN productcharacteristics pc ON ric.productcharacteristicid = pc.productcharacteristicid
        LEFT JOIN characteristics ch ON pc.characteristicid = ch.characteristicid
        WHERE ri.contractid = @ContractId
        ORDER BY ri.requestitemid;
    ";

            try
            {
                ContractDetailsDto contract = null!;

                // Получаем данные о контракте
                using (var cmd = new NpgsqlCommand(contractQuery, connection))
                {
                    cmd.Parameters.AddWithValue("@ContractId", id);
                    using var reader = await cmd.ExecuteReaderAsync();

                    if (await reader.ReadAsync())
                    {
                        byte[]? photo = reader.IsDBNull(7) ? null : (byte[])reader[7];
                        contract = new ContractDetailsDto
                        {
                            ContractID = reader.GetInt32(0),
                            ContractNumber = reader.GetString(1),
                            ContractDate = reader.GetDateTime(2),
                            StatusID = reader.GetInt32(3),
                            StatusName = reader.GetString(4),
                            ContactPhone = reader.IsDBNull(5) ? null : reader.GetString(5),
                            Description = reader.IsDBNull(6) ? null : reader.GetString(6),
                            ManualRequestPhotoBase64 = photo != null ? Convert.ToBase64String(photo) : null,
                            ActualAmount = reader.GetDecimal(8),
                            RequestID = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                            SupplierID = reader.IsDBNull(10) ? (int?)null : reader.GetInt32(10), // SupplierID может быть null
                            IKZ = reader.IsDBNull(11) ? null : reader.GetString(11),
                            ProtocolNumber = reader.IsDBNull(12) ? null : reader.GetString(12),            // 🔹
                            ProtocolDate = reader.IsDBNull(13) ? (DateTime?)null : reader.GetDateTime(13),
                            Items = new List<RequestItemDto>()
                        };
                    }
                }

                // Получаем товары из контракта
                if (contract == null)
                    return NotFound("Контракт не найден");

                using (var cmd = new NpgsqlCommand(itemQuery, connection))
                {
                    cmd.Parameters.AddWithValue("@ContractId", id);
                    using var reader = await cmd.ExecuteReaderAsync();

                    var itemDict = new Dictionary<int, RequestItemDto>();

                    while (await reader.ReadAsync())
                    {
                        var requestItemId = reader.GetInt32(0);
                        if (!itemDict.TryGetValue(requestItemId, out var item))
                        {
                            item = new RequestItemDto
                            {
                                RequestItemID = requestItemId,
                                ProductID = reader.GetInt32(1),
                                ProductName = reader.GetString(2),
                                Unit = new UnitDto
                                {
                                    UnitID = reader.GetInt32(3),
                                    UnitName = reader.GetString(4)
                                },
                                Quantity = reader.GetInt32(5),
                                UnitPrice = reader.IsDBNull(6) ? 0 : reader.GetDecimal(6),
                                RequestItemCharacteristics = new List<RequestItemCharacteristicDto>()
                            };
                            itemDict[requestItemId] = item;
                        }

                        if (!reader.IsDBNull(7))
                        {
                            var characteristic = new RequestItemCharacteristicDto
                            {
                                ProductCharacteristicID = reader.GetInt32(7),
                                ValueRequest = reader.IsDBNull(8) ? "" : reader.GetString(8),
                                ProductCharacteristic = new ProductCharacteristicDto
                                {
                                    CharacteristicID = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                                    CharacteristicName = reader.IsDBNull(10) ? "" : reader.GetString(10)
                                }
                            };

                            item.RequestItemCharacteristics.Add(characteristic);
                        }
                    }

                    contract.Items = itemDict.Values.ToList();
                }
              
                return Ok(contract);
            }
            catch (Exception ex)
            {
                return StatusCode(500, "Ошибка при получении деталей контракта: " + ex.Message);
            }
        }

        [HttpGet("suppliers/{id}")]
        public async Task<IActionResult> GetSupplier(int id)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var query = @"
SELECT SupplierID, Name, Address, INN, KPP, KPP_KN, OGRN, Phone, Email, ShortName, PostalAddress
FROM suppliers
WHERE SupplierID = @SupplierID;

";

                using var cmd = new NpgsqlCommand(query, connection);
                cmd.Parameters.AddWithValue("@SupplierID", id);

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var supplier = new SupplierDto
                    {
                        SupplierID = reader.GetInt32(0),
                        Name = reader.GetString(1),
                        Address = reader.IsDBNull(2) ? null : reader.GetString(2), // Обработка возможного отсутствия Address
                        INN = reader.IsDBNull(3) ? null : reader.GetString(3), // Обработка возможного отсутствия INN
                        KPP = reader.IsDBNull(4) ? null : reader.GetString(4), // Обработка возможного отсутствия KPP
                        KPP_KN = reader.IsDBNull(5) ? null : reader.GetString(5), // Обработка возможного отсутствия KPP_KN
                        OGRN = reader.IsDBNull(6) ? null : reader.GetString(6), // Обработка возможного отсутствия OGRN
                        Phone = reader.IsDBNull(7) ? null : reader.GetString(7), // Обработка возможного отсутствия Phone
                        Email = reader.IsDBNull(8) ? null : reader.GetString(8) ,// Обработка возможного отсутствия Email
                        ShortName = reader.IsDBNull(9) ? null : reader.GetString(9),
                        PostalAddress = reader.IsDBNull(10) ? null : reader.GetString(10)
                    };

                    return Ok(supplier);
                }

                return NotFound("Поставщик не найден");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Ошибка при получении данных о поставщике: {ex.Message}");
            }
        }

        [HttpPut("suppliers/{id}")]
        public async Task<IActionResult> UpdateSupplier(int id, [FromBody] SupplierDto dto)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            try
            {
                var updateQuery = @"
         UPDATE suppliers
SET 
    name = @Name,
    address = @Address,
    inn = @INN,
    kpp = @KPP,
    kpp_kn = @KPP_KN,
    ogrn = @OGRN,
    phone = @Phone,
    email = @Email,
    shortname = @ShortName,
    postaladdress = @PostalAddress
WHERE supplierid = @SupplierID;

        ";

                using var cmd = new NpgsqlCommand(updateQuery, connection);
                cmd.Parameters.AddWithValue("@Name", dto.Name ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Address", dto.Address ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@INN", dto.INN ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@KPP", dto.KPP ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@KPP_KN", dto.KPP_KN ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@OGRN", dto.OGRN ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Phone", dto.Phone ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Email", dto.Email ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@SupplierID", id);
                cmd.Parameters.AddWithValue("@ShortName", dto.ShortName ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@PostalAddress", dto.PostalAddress ?? (object)DBNull.Value);


                int affectedRows = await cmd.ExecuteNonQueryAsync();
                if (affectedRows == 0)
                    return NotFound("Поставщик не найден.");

                return Ok("Данные поставщика обновлены.");
            }
            catch (Exception ex)
            {
                return StatusCode(500, "Ошибка при обновлении поставщика: " + ex.Message);
            }
        }

        [HttpPost("Addsuppliers")]
        public async Task<IActionResult> AddSupplier([FromBody] SupplierDto dto)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("Authorization", "Token 91bca5744375b62873ca02ba6acb51e66f6261b9");

                var jsonBody = System.Text.Json.JsonSerializer.Serialize(new { query = dto.INN });
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                var response = await client.PostAsync("https://suggestions.dadata.ru/suggestions/api/4_1/rs/findById/party", content);
                var raw = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return StatusCode((int)response.StatusCode, "Ошибка DaData: " + raw);

                var json = JsonSerializer.Deserialize<DaDataResponse>(raw, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                var party = json?.Suggestions?.FirstOrDefault()?.Data;

                if (party == null)
                    return NotFound("Организация по ИНН не найдена.");

                if (party.State?.Status?.ToUpper() != "ACTIVE")
                    return BadRequest($"Поставщик не может быть добавлен. Статус: {party.State?.Status?.ToUpper()}");

                var checkQuery = @"
SELECT supplierid FROM suppliers
WHERE inn = @INN AND kpp = @KPP;
";

                using var checkCmd = new NpgsqlCommand(checkQuery, connection);
                checkCmd.Parameters.AddWithValue("@INN", dto.INN ?? (object)DBNull.Value);
                checkCmd.Parameters.AddWithValue("@KPP", dto.KPP ?? (object)DBNull.Value);

                var existingId = await checkCmd.ExecuteScalarAsync();
                if (existingId != null && existingId != DBNull.Value)
                {
                    // уже существует, возвращаем его
                    dto.SupplierID = (int)existingId;
                    return Ok(dto);
                }

                var insertQuery = @"
           INSERT INTO suppliers (name, address, inn, kpp, kpp_kn, ogrn, phone, email, shortname, postaladdress)
VALUES (@Name, @Address, @INN, @KPP, @KPP_KN, @OGRN, @Phone, @Email, @ShortName, @PostalAddress)
RETURNING supplierid;

        ";

                using var cmd = new NpgsqlCommand(insertQuery, connection);
                cmd.Parameters.AddWithValue("@Name", dto.Name ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Address", dto.Address ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@INN", dto.INN ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@KPP", dto.KPP ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@KPP_KN", dto.KPP_KN ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@OGRN", dto.OGRN ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Phone", dto.Phone ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Email", dto.Email ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ShortName", dto.ShortName ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@PostalAddress", dto.PostalAddress ?? (object)DBNull.Value);


                var newId = (int)await cmd.ExecuteScalarAsync();
                dto.SupplierID = newId;

                return Ok(dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, "Ошибка при добавлении поставщика: " + ex.Message);
            }
        }


        [HttpPut("link-supplier/{contractId}")]
        public async Task<IActionResult> LinkSupplierToContract(int contractId, [FromBody] LinkSupplierDto dto)
        {
            if (dto == null || dto.SupplierID <= 0)
                return BadRequest("Некорректный ID поставщика.");

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
        UPDATE contracts
        SET supplierid = @SupplierID
        WHERE contractid = @ContractID;
    ";

            using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@SupplierID", dto.SupplierID);
            cmd.Parameters.AddWithValue("@ContractID", contractId);

            int affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0)
                return NotFound("Контракт не найден.");

            return Ok("Поставщик успешно привязан к контракту.");
        }

        [HttpGet("suppliers")]
        public async Task<IActionResult> GetAllSuppliers()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var suppliers = new List<SupplierDto>();

            var query = @"
          SELECT 
            supplierid, name, address, inn, kpp, kpp_kn, ogrn, phone, email, shortname, postaladdress
        FROM suppliers
        ORDER BY name;
    ";

            using var cmd = new NpgsqlCommand(query, connection);
            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                suppliers.Add(new SupplierDto
                {
                    SupplierID = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Address = reader.IsDBNull(2) ? null : reader.GetString(2),
                    INN = reader.IsDBNull(3) ? null : reader.GetString(3),
                    KPP = reader.IsDBNull(4) ? null : reader.GetString(4),
                    KPP_KN = reader.IsDBNull(5) ? null : reader.GetString(5),
                    OGRN = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Phone = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Email = reader.IsDBNull(8) ? null : reader.GetString(8),
                    ShortName = reader.IsDBNull(9) ? null : reader.GetString(9),
                    PostalAddress = reader.IsDBNull(10) ? null : reader.GetString(10)
                });
            }

            return Ok(suppliers);
        }

        public class LinkSupplierDto
        {
            public int SupplierID { get; set; }
        }






        public class SupplierDto
        {
            public int SupplierID { get; set; }
            public string Name { get; set; }
            public string? Address { get; set; }  // Nullable, так как поле может отсутствовать
            public string? INN { get; set; }  // Nullable, так как поле может отсутствовать
            public string? KPP { get; set; }  // Nullable, так как поле может отсутствовать
            public string? KPP_KN { get; set; }  // Nullable, так как поле может отсутствовать
            public string? OGRN { get; set; }  // Nullable, так как поле может отсутствовать
            public string? Phone { get; set; }  // Nullable, так как поле может отсутствовать
            public string? Email { get; set; }  // Nullable, так как поле может отсутствовать
            public string? ShortName { get; set; }
            public string? PostalAddress { get; set; }

        }





        public class ContractDetailsDto
        {
            public int ContractID { get; set; }
            public string ContractNumber { get; set; }
            public DateTime ContractDate { get; set; }
            public int StatusID { get; set; }
            public string StatusName { get; set; }
            public string? ContactPhone { get; set; }
            public string? Description { get; set; }
            public string? ManualRequestPhotoBase64 { get; set; }
            public decimal ActualAmount { get; set; }
            public int? RequestID { get; set; } // 👈 Добавить это свойство
            public int? SupplierID { get; set; }  // Сделано nullable для возможности отсутствия поставщика
            public string? IKZ { get; set; } // ✅ ← ДОБАВЬ ЭТУ СТРОКУ
            public string? ProtocolNumber { get; set; }
            public DateTime? ProtocolDate { get; set; }


            public SupplierDto Supplier { get; set; } //

            public List<RequestItemDto> Items { get; set; }
        }

        public class ContractDetailsDto1
        {
            public int ContractID { get; set; }
            public string ContractNumber { get; set; }
            public DateTime ContractDate { get; set; }
            public string? Description { get; set; }
            public string? ManualRequestPhotoBase64 { get; set; } // Base64-картинка (может быть null)
            public int StatusId { get; set; }
            public decimal ActualAmount { get; set; }
            public int? RequestID { get; set; }

            public List<RequestItemDto> Items { get; set; } = new();
        }



        [HttpPut("update/{id}")]
        public async Task<IActionResult> UpdateContract(int id, [FromBody] ContractDetailsDto1 dto)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = await connection.BeginTransactionAsync();

            try
            {
                // Проверка: привязан ли контракт к заявке
                int? requestId = null;

                var getRequestIdCmd = new NpgsqlCommand("SELECT requestid FROM contracts WHERE contractid = @ContractId", connection);
                getRequestIdCmd.Parameters.AddWithValue("@ContractId", id);

                var result = await getRequestIdCmd.ExecuteScalarAsync();
                if (result != DBNull.Value && result != null)
                    requestId = Convert.ToInt32(result);

                // Проверка: если нет заявки, а фото не передано — ошибка
                if (requestId == null && string.IsNullOrWhiteSpace(dto.ManualRequestPhotoBase64))
                {
                    return BadRequest("Загрузите фото заявки, так как контракт не привязан к заявке.");
                }

                // Обработка фото
                byte[]? photoBytes = null;
                if (!string.IsNullOrWhiteSpace(dto.ManualRequestPhotoBase64))
                {
                    try
                    {
                        photoBytes = Convert.FromBase64String(dto.ManualRequestPhotoBase64);
                    }
                    catch
                    {
                        return BadRequest("Некорректный формат изображения. Проверьте Base64.");
                    }
                }

                // Обновление контракта
                var updateContractQuery = @"
            UPDATE contracts
            SET 
                statusid = @StatusId,
                description = @Description,
                manual_request_photo = @Photo,
                actualamount = @ActualAmount
            WHERE contractid = @ContractId;
        ";

                using (var cmd = new NpgsqlCommand(updateContractQuery, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@StatusId", dto.StatusId);
                    cmd.Parameters.AddWithValue("@Description", (object?)dto.Description ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Photo", (object?)photoBytes ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ActualAmount", dto.ActualAmount);
                    cmd.Parameters.AddWithValue("@ContractId", id);

                    await cmd.ExecuteNonQueryAsync();
                }

                // Обновление товаров
                foreach (var item in dto.Items)
                {
                    if (item.Quantity <= 0 || item.UnitPrice <= 0)
                    {
                        return BadRequest($"У товара с ID {item.RequestItemID} должно быть указано положительное количество и цена.");
                    }

                    var updateItemQuery = @"
                UPDATE requestitems
                SET quantity = @Quantity, unitprice = @UnitPrice
                WHERE requestitemid = @RequestItemId;
            ";

                    using var cmd = new NpgsqlCommand(updateItemQuery, connection, transaction);
                    cmd.Parameters.AddWithValue("@Quantity", item.Quantity);
                    cmd.Parameters.AddWithValue("@UnitPrice", item.UnitPrice);
                    cmd.Parameters.AddWithValue("@RequestItemId", item.RequestItemID);

                    await cmd.ExecuteNonQueryAsync();
                    foreach (var characteristic in item.RequestItemCharacteristics)
                    {
                        if (string.IsNullOrWhiteSpace(characteristic.ValueRequest))
                        {
                            return BadRequest($"Для характеристики товара с ID {item.RequestItemID} значение характеристики не может быть пустым.");
                        }

                        var updateCharacteristicQuery = @"
                UPDATE requestitemcharacteristics
                SET valuerequest = @ValueRequest
                WHERE requestitemid = @RequestItemId
                AND productcharacteristicid = @ProductCharacteristicID;
                ";

                        using var charCmd = new NpgsqlCommand(updateCharacteristicQuery, connection, transaction);
                        charCmd.Parameters.AddWithValue("@ValueRequest", characteristic.ValueRequest);
                        charCmd.Parameters.AddWithValue("@RequestItemId", item.RequestItemID);
                        charCmd.Parameters.AddWithValue("@ProductCharacteristicID", characteristic.ProductCharacteristicID);

                        await charCmd.ExecuteNonQueryAsync();
                    }

                }

                await transaction.CommitAsync();
                return Ok(new { message = "Контракт успешно обновлён" });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, "Ошибка при обновлении контракта: " + ex.Message);
            }
        }



    }
}
