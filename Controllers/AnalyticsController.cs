using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Data;
using Xceed.Document.NET;
using Xceed.Words.NET;

namespace APIdIplom.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AnalyticsController : ControllerBase
    {
        private readonly IConfiguration _configuration;

        public AnalyticsController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        [HttpGet("category-summary")]
        public async Task<IActionResult> GetCategorySummary([FromQuery] int year)
        {
            var result = new List<CategorySummaryDto>();
            var connectionString = _configuration.GetConnectionString("DefaultConnection");

            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            var sql = @"
        SELECT c.name AS category, SUM(ri.quantity * ri.unitprice) AS amount
        FROM requestitems ri
        JOIN products p ON ri.productid = p.productid
        JOIN categories c ON p.categoryid = c.categoryid
        JOIN contracts ct ON ri.contractid = ct.contractid
        WHERE ct.actualamount > 0 AND EXTRACT(YEAR FROM ct.contractdate) = @year
        GROUP BY c.name
        ORDER BY amount DESC;";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@year", year);

            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                result.Add(new CategorySummaryDto
                {
                    Category = reader.GetString(0),
                    Amount = reader.IsDBNull(1) ? 0 : reader.GetDecimal(1)
                });
            }

            return Ok(result);
        }


        public class CategorySummaryDto
        {
            public string Category { get; set; }
            public decimal Amount { get; set; }
        }

        [HttpGet("report")]
        public IActionResult GenerateCategoryReport(
    [FromQuery] int year,
    [FromQuery] string firstName,
    [FromQuery] string middleName,
    [FromQuery] string lastName,
    [FromQuery] string userRole)
        {
            var connectionString = _configuration.GetConnectionString("DefaultConnection");
            var data = new List<CategorySummaryDto>();

            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();

            var sql = @"
    SELECT c.name AS category, SUM(ri.quantity * ri.unitprice) AS amount
    FROM requestitems ri
    JOIN products p ON ri.productid = p.productid
    JOIN categories c ON p.categoryid = c.categoryid
    JOIN contracts ct ON ri.contractid = ct.contractid
    WHERE ct.actualamount > 0 AND EXTRACT(YEAR FROM ct.contractdate) = @year
    GROUP BY c.name
    ORDER BY amount DESC;";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@year", year);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                data.Add(new CategorySummaryDto
                {
                    Category = reader.GetString(0),
                    Amount = reader.IsDBNull(1) ? 0 : reader.GetDecimal(1)
                });
            }

            // === Формирование документа ===
            using var doc = DocX.Create("report.docx");

            // Шапка документа
            doc.InsertParagraph("Муниципальное бюджетное общеобразовательное учреждение\n«Образовательный комплекс им. Владимира Храброго»")
                .Font("Times New Roman")
                .FontSize(12)
                .Alignment = Alignment.center;

            var approveParagraph = doc.InsertParagraph("УТВЕРЖДАЮ\nДиректор школы _____________ Ненашева Олеся Александровна")
      .Font("Times New Roman")
      .FontSize(12);
            approveParagraph.Alignment = Alignment.right;
            approveParagraph.SpacingAfter(20);

 

            var titleParagraph = doc.InsertParagraph($"ОТЧЁТ\nо закупках товаров по категориям за {year} год")
                .Font("Times New Roman")
                .FontSize(14)
                .Bold();
            titleParagraph.Alignment = Alignment.center;
            titleParagraph.SpacingAfter(20); // ✅ правильно

            // Вставляем данные о сотруднике
            var fromWhom = doc.InsertParagraph($"От: {userRole} {lastName} {firstName} {middleName}")
                .Font("Times New Roman")
                .FontSize(12)
                .Alignment = Alignment.right;
           

            // Таблица
            var table = doc.AddTable(data.Count + 1, 3);
            table.Design = TableDesign.TableGrid;
            table.Alignment = Alignment.center;

            table.Rows[0].Cells[0].Paragraphs[0].Append("№").Bold();
            table.Rows[0].Cells[1].Paragraphs[0].Append("Категория").Bold();
            table.Rows[0].Cells[2].Paragraphs[0].Append("Сумма (₽)").Bold();

            for (int i = 0; i < data.Count; i++)
            {
                table.Rows[i + 1].Cells[0].Paragraphs[0].Append((i + 1).ToString());
                table.Rows[i + 1].Cells[1].Paragraphs[0].Append(data[i].Category);
                table.Rows[i + 1].Cells[2].Paragraphs[0].Append($"{data[i].Amount:N0} ₽");
            }

            var totalAmount = data.Sum(d => d.Amount);
            var totalRow = table.InsertRow();
            totalRow.Cells[1].Paragraphs[0].Append("Итого").Bold();
            totalRow.Cells[2].Paragraphs[0].Append($"{totalAmount:N0} ₽");

            doc.InsertTable(table);
            doc.InsertParagraph().SpacingAfter(20);

            // Подпись и дата
            doc.InsertParagraph($"Составил(а): {userRole} _______________________ {lastName} {firstName} {middleName}")
                .Font("Times New Roman")
                .FontSize(12)
                .SpacingAfter(10);

            doc.InsertParagraph($"Дата составления: {DateTime.Now:dd.MM.yyyy}")
                .Font("Times New Roman")
                .FontSize(12);

            // Сохранение
            using var ms = new MemoryStream();
            doc.SaveAs(ms);
            ms.Seek(0, SeekOrigin.Begin);

            return File(ms.ToArray(),
                        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                        $"Отчет_Закупки_{year}.docx");
        }




    }
}
