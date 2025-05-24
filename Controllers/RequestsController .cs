using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Data;
using APIdIplom.Models;
using Microsoft.Extensions.Configuration;
using System.Reflection.PortableExecutable;
using Xceed.Document.NET;
using Xceed.Words.NET;
using QRCoder;
using Microsoft.AspNetCore.Authorization;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Security.Cryptography.Pkcs;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using DocumentFormat.OpenXml.Office2010.Word;
using Xceed.Drawing;
using System.Security.Claims;
using static APIdIplom.Controllers.UserController;
using System.Text.Json;
using System.Globalization;
using Svix.Client;
using System.Net.Mail;

namespace APIdIplom.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class RequestsController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly ILogger<RequestsController> _logger;
        private readonly IConfiguration _configuration;


        public RequestsController(IConfiguration configuration, ILogger<RequestsController> logger)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
            _logger = logger;
            _configuration = configuration;

        }



        [HttpGet("with-details")]
        public async Task<IActionResult> GetRequestsWithDetails()
        {
            var requests = new List<Request>();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            string query = @"
SELECT 
    r.requestid,
    r.customerid,
    c.name AS customername,
    r.requestdate,
    r.statusid,
    s.name AS statusname,
    r.totalamount,
    r.description,
    r.signedbyuserid,
    r.signeddate,
    ct.contractid,
    ct.contractnumber,
    cs.name AS contractstatusname,
    ct.actualamount AS contractamount,
    r.createdbyuserid,
    r.completedbyuserid
FROM requests r
JOIN customers c ON r.customerid = c.customerid
JOIN statuses s ON r.statusid = s.statusid
LEFT JOIN contracts ct ON ct.requestid = r.requestid
LEFT JOIN statuses_contract cs ON ct.statusid = cs.statusid;
";

            var requestMap = new Dictionary<int, Request>();

            await using (var cmd = new NpgsqlCommand(query, connection))
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var request = new Request
                    {
                        RequestID = reader.GetInt32(0),
                        CustomerID = reader.GetInt32(1),
                        RequestDate = reader.GetDateTime(3),
                        StatusID = reader.GetInt32(4),
                        TotalAmount = reader.IsDBNull(6) ? 0 : reader.GetDecimal(6),
                        Description = reader.IsDBNull(7) ? null : reader.GetString(7),
                        SignedDate = reader.IsDBNull(9) ? (DateTime?)null : reader.GetDateTime(9),
                        ContractID = reader.IsDBNull(10) ? (int?)null : reader.GetInt32(10),
                        ContractNumber = reader.IsDBNull(11) ? null : reader.GetString(11),
                        ContractStatusName = reader.IsDBNull(12) ? null : reader.GetString(12),
                        ContractAmount = reader.IsDBNull(13) ? (decimal?)null : reader.GetDecimal(13),
                        CreatedByUserId = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                        CompletedByUserId = reader.IsDBNull(15) ? null : reader.GetInt32(15),
                        Customer = new Customer
                        {
                            CustomerID = reader.GetInt32(1),
                            Name = reader.GetString(2)
                        },
                        Status = new Status
                        {
                            StatusID = reader.GetInt32(4),
                            StatusName = reader.GetString(5)
                        },
                        Signatures = new List<RequestSignatureInfo>()
                    };

                    requests.Add(request);
                    requestMap[request.RequestID] = request;
                }
            }

            // Загружаем подписи
            string signQuery = @"
SELECT rs.requestid, u.userid, u.firstname, u.middlename, u.lastname, r.name as rolename, rs.signeddate
FROM requestsignatures rs
JOIN users u ON rs.userid = u.userid
JOIN role r ON u.role_id = r.roleid;";

            await using (var signCmd = new NpgsqlCommand(signQuery, connection))
            await using (var reader = await signCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var requestId = reader.GetInt32(0);
                    if (!requestMap.ContainsKey(requestId)) continue;

                    var signature = new RequestSignatureInfo
                    {
                        UserId = reader.GetInt32(1),
                        FullName = $"{reader.GetString(4)} {reader.GetString(2)} {reader.GetString(3)}",
                        Role = reader.GetString(5),
                        SignedDateTime = reader.GetDateTime(6)
                    };

                    requestMap[requestId].Signatures.Add(signature);
                }
            }

            return Ok(requests);
        }



        [HttpGet("with-characteristics")]
        public async Task<IActionResult> GetProductsWithCharacteristics()
        {
            try
            {
                var products = new List<Products>();

                using (var connection = new NpgsqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    // Обновленный запрос без unitprice
                    string query = @"
                SELECT 
                    p.productid,
                    p.name AS productname,
                    p.unitid,
                    u.unitname,
                    p.categoryid,
                    cat.name AS categoryname,
                    pc.productcharacteristicid,
                    pc.characteristicid,
                    c.name AS characteristicname
                FROM products p
                JOIN units u ON p.unitid = u.unitid
                JOIN categories cat ON p.categoryid = cat.categoryid
                LEFT JOIN productcharacteristics pc ON p.productid = pc.productid
                LEFT JOIN characteristics c ON pc.characteristicid = c.characteristicid
                ORDER BY p.productid";

                    using (var cmd = new NpgsqlCommand(query, connection))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        var productDictionary = new Dictionary<int, Products>();

                        while (await reader.ReadAsync())
                        {
                            var productId = reader.GetInt32(0);

                            if (!productDictionary.TryGetValue(productId, out var product))
                            {
                                product = new Products
                                {
                                    ProductID = productId,
                                    Name = reader.GetString(1),
                                    UnitID = reader.GetInt32(2),
                                    Unit = new Unit
                                    {
                                        UnitID = reader.GetInt32(2),
                                        Name = reader.GetString(3)
                                    },
                                    CategoryID = reader.GetInt32(4),
                                    Category = new Category
                                    {
                                        CategoryID = reader.GetInt32(4),
                                        Name = reader.GetString(5)
                                    },
                                    ProductCharacteristics = new List<ProductCharacteristics>()
                                };

                                productDictionary.Add(productId, product);
                            }

                            // Добавляем характеристики, если они есть
                            if (!reader.IsDBNull(6))
                            {
                                product.ProductCharacteristics.Add(new ProductCharacteristics
                                {
                                    ProductCharacteristicID = reader.GetInt32(6),
                                    ProductID = productId,
                                    CharacteristicID = reader.GetInt32(7),
                                    Characteristic = new Models.Characteristics
                                    {
                                        CharacteristicID = reader.GetInt32(7),
                                        Name = reader.GetString(8)
                                    }
                                });
                            }
                        }

                        products = productDictionary.Values.ToList();
                    }
                }

                return Ok(products);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        [HttpGet("statuses")]
        public async Task<IActionResult> GetStatuses()
        {
            var statuses = new List<Status>();

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                // Запрос для получения всех статусов
                string query = @"
            SELECT 
                statusid,
                name
            FROM statuses";

                using (var cmd = new NpgsqlCommand(query, connection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var status = new Status
                        {
                            StatusID = reader.GetInt32(0),
                            StatusName = reader.GetString(1)
                        };

                        statuses.Add(status);
                    }
                }
            }

            return Ok(statuses);
        }

        public class SaveRequestModel
        {
            public int CustomerID { get; set; }
            public int StatusID { get; set; }
            public string Description { get; set; }
            public List<RequestItemModel> RequestItems { get; set; }
        }

        public class RequestItemCharacteristicModel
        {
            public int ProductCharacteristicID { get; set; }
            public string ValueRequest { get; set; }
        }

        public class RequestItemModel
        {
            public int ProductID { get; set; }
            public int Quantity { get; set; }
            public decimal UnitPrice { get; set; }

            public List<RequestItemCharacteristicModel> RequestItemCharacteristics { get; set; }
        }



        [Authorize]
        [HttpPost("save-request")]
        public async Task<IActionResult> SaveRequest([FromBody] SaveRequestModel model)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var transaction = connection.BeginTransaction();

            try
            {
                var userId = await GetCurrentUserId(connection);
                if (userId == null)
                    return Unauthorized("Не удалось определить пользователя");

                var requestId = await InsertRequestAsync(connection, transaction, model, userId.Value);



                foreach (var item in model.RequestItems)
                {
                    int requestItemId = await InsertRequestItemAsync(connection, transaction, requestId, item);

                    if (item.RequestItemCharacteristics != null)
                    {
                        foreach (var characteristic in item.RequestItemCharacteristics)
                        {
                            await InsertRequestItemCharacteristicAsync(connection, transaction, requestItemId, characteristic);
                        }
                    }
                }


                await transaction.CommitAsync();
                return Ok(new { RequestID = requestId });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, $"Ошибка при сохранении заявки: {ex.Message}");
            }
        }
        private async Task<int> InsertRequestAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SaveRequestModel model, int userId)
        {
            string query = @"
        INSERT INTO requests 
        (customerid, statusid, requestdate, totalamount, description, createdbyuserid)
        VALUES (@CustomerID, @StatusID, @RequestDate, @TotalAmount, @Description, @CreatedByUserId)
        RETURNING requestid;";

            using (var cmd = new NpgsqlCommand(query, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@CustomerID", model.CustomerID);
                cmd.Parameters.AddWithValue("@StatusID", model.StatusID);
                cmd.Parameters.AddWithValue("@RequestDate", DateTime.UtcNow);
                cmd.Parameters.AddWithValue("@TotalAmount", model.RequestItems.Sum(item => item.Quantity * item.UnitPrice));
                cmd.Parameters.AddWithValue("@Description", model.Description);
                cmd.Parameters.AddWithValue("@CreatedByUserId", userId);

                var requestId = await cmd.ExecuteScalarAsync();
                return Convert.ToInt32(requestId);
            }
        }



        private async Task InsertRequestItemCharacteristicAsync(
     NpgsqlConnection connection,
     NpgsqlTransaction transaction,
     int requestItemId,
     RequestItemCharacteristicModel characteristic)
        {
            string query = @"
        INSERT INTO requestitemcharacteristics (requestitemid, valuerequest, productcharacteristicid)
        VALUES (@RequestItemID, @ValueRequest, @ProductCharacteristicID);";

            using (var cmd = new NpgsqlCommand(query, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@RequestItemID", requestItemId);
                cmd.Parameters.AddWithValue("@ValueRequest", characteristic.ValueRequest);
                cmd.Parameters.AddWithValue("@ProductCharacteristicID", characteristic.ProductCharacteristicID);

                await cmd.ExecuteNonQueryAsync();
            }
        }


        private async Task<int> InsertRequestItemAsync(
     NpgsqlConnection connection,
     NpgsqlTransaction transaction,
     int requestId,
     RequestItemModel item)
        {
            string query = @"
        INSERT INTO requestitems (requestid, productid, quantity, unitprice, totalprice)
        VALUES (@RequestID, @ProductID, @Quantity, @UnitPrice, @TotalPrice)
        RETURNING requestitemid;";

            using (var cmd = new NpgsqlCommand(query, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@RequestID", requestId);
                cmd.Parameters.AddWithValue("@ProductID", item.ProductID);
                cmd.Parameters.AddWithValue("@Quantity", item.Quantity);
                cmd.Parameters.AddWithValue("@UnitPrice", item.UnitPrice);
                cmd.Parameters.AddWithValue("@TotalPrice", item.Quantity * item.UnitPrice);

                var result = await cmd.ExecuteScalarAsync();
                return Convert.ToInt32(result);
            }
        }

        public class RequestDetailsDto
        {
            public int RequestID { get; set; }
            public int CustomerID { get; set; }
            public int StatusID { get; set; }
            public string Description { get; set; }
            public DateTime RequestDate { get; set; }
            public int CreatedByUserId { get; set; }


            public List<RequestItemDto> RequestItems { get; set; }
        }

        public class RequestItemDto
        {
            public int RequestItemID { get; set; } // 👈 ВАЖНО

            public int ProductID { get; set; }
            public string ProductName { get; set; }
            public UnitDto Unit { get; set; }
            public decimal UnitPrice { get; set; }
            public int Quantity { get; set; }

            public List<RequestItemCharacteristicDto> RequestItemCharacteristics { get; set; }
        }

        public class RequestItemCharacteristicDto
        {
            public int RequestItemID { get; set; } // 👈 ВАЖНО

            public int ProductCharacteristicID { get; set; }
            public string ValueRequest { get; set; }
            public ProductCharacteristicDto ProductCharacteristic { get; set; }
        }

        public class ProductCharacteristicDto
        {
            public int CharacteristicID { get; set; }
            public string CharacteristicName { get; set; }
        }

        public class UnitDto
        {
            public int UnitID { get; set; }
            public string UnitName { get; set; }
        }


        [HttpGet("{id}")]
        public async Task<IActionResult> GetRequestById(int id)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var requestDto = new RequestDetailsDto
            {
                RequestItems = new List<RequestItemDto>()
            };

            var query = @"
SELECT 
    r.requestid, r.customerid, r.statusid, r.description, r.requestdate,
    r.createdbyuserid,
    ri.requestitemid, ri.productid, p.name AS productname,
    u.unitid, u.unitname,
    ri.unitprice, ri.quantity,
    ric.valuerequest,
    pc.productcharacteristicid,
    c.characteristicid, c.name AS characteristicname
FROM requests r
JOIN requestitems ri ON r.requestid = ri.requestid
JOIN products p ON ri.productid = p.productid
JOIN units u ON p.unitid = u.unitid
LEFT JOIN requestitemcharacteristics ric ON ric.requestitemid = ri.requestitemid
LEFT JOIN productcharacteristics pc ON ric.productcharacteristicid = pc.productcharacteristicid
LEFT JOIN characteristics c ON pc.characteristicid = c.characteristicid
WHERE r.requestid = @requestid;";

            var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@requestid", id);

            var reader = await cmd.ExecuteReaderAsync();
            var itemMap = new Dictionary<int, RequestItemDto>();

            while (await reader.ReadAsync())
            {
                if (requestDto.RequestID == 0)
                {
                    requestDto.RequestID = reader.GetInt32(0);
                    requestDto.CustomerID = reader.GetInt32(1);
                    requestDto.StatusID = reader.GetInt32(2);
                    requestDto.Description = reader.IsDBNull(3) ? null : reader.GetString(3);
                    requestDto.RequestDate = reader.GetDateTime(4);
                    requestDto.CreatedByUserId = reader.GetInt32(5);

                }

                int requestItemId = reader.GetInt32(6);

                if (!itemMap.TryGetValue(requestItemId, out var item))
                {
                    item = new RequestItemDto
                    {
                        RequestItemID = requestItemId,
                        ProductID = reader.GetInt32(7),
                        ProductName = reader.GetString(8),
                        Unit = new UnitDto
                        {
                            UnitID = reader.GetInt32(9),
                            UnitName = reader.GetString(10)
                        },
                        UnitPrice = reader.GetDecimal(11),
                        Quantity = reader.GetInt32(12),
                        RequestItemCharacteristics = new List<RequestItemCharacteristicDto>()
                    };

                    itemMap[requestItemId] = item;
                    requestDto.RequestItems.Add(item);
                }


                if (!reader.IsDBNull(13))
                {
                    item.RequestItemCharacteristics.Add(new RequestItemCharacteristicDto
                    {
                        ProductCharacteristicID = reader.GetInt32(14),
                        ValueRequest = reader.GetString(13),
                        ProductCharacteristic = new ProductCharacteristicDto
                        {
                            CharacteristicID = reader.GetInt32(15),
                            CharacteristicName = reader.GetString(16)
                        }
                    });
                }

            }

            return Ok(requestDto);
        }

        protected async Task<int?> GetCurrentUserId(NpgsqlConnection connection)
        {
            var username = User?.Identity?.Name;
            if (string.IsNullOrEmpty(username))
                return null;

            var cmd = new NpgsqlCommand("SELECT userid FROM users WHERE username = @username", connection);
            cmd.Parameters.AddWithValue("@username", username);

            var result = await cmd.ExecuteScalarAsync();
            return result != null ? Convert.ToInt32(result) : (int?)null;
        }

        [Authorize]
        [HttpPut("{id}/status")]
        public async Task<IActionResult> UpdateStatus(int id, [FromBody] StatusUpdateDto model)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var userId = await GetCurrentUserId(connection);

            string query;
            if (model.StatusID == 3) // Завершена
            {
                query = @"
            UPDATE requests
            SET statusid = @StatusID,
                completedbyuserid = @CompletedByUserId,
                completeddate = NOW()
            WHERE requestid = @RequestID;";
            }
            else
            {
                query = "UPDATE requests SET statusid = @StatusID WHERE requestid = @RequestID;";
            }

            var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@StatusID", model.StatusID);
            cmd.Parameters.AddWithValue("@RequestID", id);

            if (model.StatusID == 3)
                cmd.Parameters.AddWithValue("@CompletedByUserId", userId);

            var affected = await cmd.ExecuteNonQueryAsync();

            if (affected > 0)
            {
                if (model.StatusID == 3)
                {
                    // Вызов метода уведомления
                    await NotifyPendingSignatures(id);
                }
                return Ok();
            }
            else
            {
                return NotFound("Заявка не найдена");
            }

        }


        public class StatusUpdateDto
        {
            public int StatusID { get; set; }
        }


       

        // DTO для возврата данных
        public class UserForRequestDto
        {
            public int Id { get; set; }
            public string FullName { get; set; }
            public string Role { get; set; }
        }
        [HttpGet("categories")]
        public async Task<IActionResult> GetAllCategories()
        {
            try
            {
                var categories = new List<Category>();

                using (var connection = new NpgsqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    string query = "SELECT categoryid, name FROM categories ORDER BY name";

                    using (var cmd = new NpgsqlCommand(query, connection))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            categories.Add(new Category
                            {
                                CategoryID = reader.GetInt32(0),
                                Name = reader.GetString(1)
                            });
                        }
                    }
                }

                return Ok(categories);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        [HttpPost("add-characteristic")]
        public async Task<IActionResult> AddCharacteristicToProduct([FromBody] AddCharacteristicDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.CharacteristicName))
                return BadRequest("Название характеристики не может быть пустым.");

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                // Проверка: существует ли уже характеристика
                int characteristicId;

                var checkCharacteristicQuery = "SELECT characteristicid FROM characteristics WHERE LOWER(name) = LOWER(@Name) LIMIT 1";
                await using (var checkCmd = new NpgsqlCommand(checkCharacteristicQuery, connection, transaction))
                {
                    checkCmd.Parameters.AddWithValue("@Name", dto.CharacteristicName);
                    var result = await checkCmd.ExecuteScalarAsync();

                    if (result != null)
                    {
                        characteristicId = Convert.ToInt32(result);
                    }
                    else
                    {
                        // Добавление новой характеристики
                        var insertCharacteristicQuery = "INSERT INTO characteristics (name) VALUES (@Name) RETURNING characteristicid";
                        await using var insertCmd = new NpgsqlCommand(insertCharacteristicQuery, connection, transaction);
                        insertCmd.Parameters.AddWithValue("@Name", dto.CharacteristicName);
                        characteristicId = Convert.ToInt32(await insertCmd.ExecuteScalarAsync());
                    }
                }

                // Проверка: уже связана ли характеристика с товаром
                var checkLinkQuery = @"
            SELECT COUNT(*) FROM productcharacteristics
            WHERE productid = @ProductId AND characteristicid = @CharacteristicId";
                await using (var linkCmd = new NpgsqlCommand(checkLinkQuery, connection, transaction))
                {
                    linkCmd.Parameters.AddWithValue("@ProductId", dto.ProductId);
                    linkCmd.Parameters.AddWithValue("@CharacteristicId", characteristicId);

                    var count = Convert.ToInt32(await linkCmd.ExecuteScalarAsync());
                    if (count == 0)
                    {
                        // Добавление связи productcharacteristics
                        var insertLinkQuery = @"
                    INSERT INTO productcharacteristics (productid, characteristicid)
                    VALUES (@ProductId, @CharacteristicId)";
                        await using var insertLinkCmd = new NpgsqlCommand(insertLinkQuery, connection, transaction);
                        insertLinkCmd.Parameters.AddWithValue("@ProductId", dto.ProductId);
                        insertLinkCmd.Parameters.AddWithValue("@CharacteristicId", characteristicId);
                        await insertLinkCmd.ExecuteNonQueryAsync();
                    }
                }

                await transaction.CommitAsync();
                return Ok(new { message = "Характеристика успешно добавлена." });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, $"Ошибка при добавлении: {ex.Message}");
            }
        }

        // DTO
        public class AddCharacteristicDto
        {
            public int ProductId { get; set; }
            public string CharacteristicName { get; set; }
        }

        [HttpGet("all-characteristics")]
        public async Task<IActionResult> GetAllCharacteristics()
        {
            var characteristics = new List<Models.Characteristics>();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = "SELECT characteristicid, name FROM characteristics ORDER BY name";

            using var cmd = new NpgsqlCommand(query, connection);
            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                characteristics.Add(new Models.Characteristics
                {
                    CharacteristicID = reader.GetInt32(0),
                    Name = reader.GetString(1)
                });
            }

            return Ok(characteristics);
        }

        [HttpPost("bind-characteristic")]
        public async Task<IActionResult> BindCharacteristicToProduct([FromBody] BindCharacteristicModel model)
        {
            if (model.ProductId <= 0 || model.CharacteristicID <= 0)
                return BadRequest("Некорректные данные");

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
        INSERT INTO productcharacteristics (productid, characteristicid)
        VALUES (@ProductID, @CharacteristicID)
        ON CONFLICT DO NOTHING;";

            using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@ProductID", model.ProductId);
            cmd.Parameters.AddWithValue("@CharacteristicID", model.CharacteristicID);

            await cmd.ExecuteNonQueryAsync();

            return Ok("Характеристика успешно связана с товаром");
        }

        public class BindCharacteristicModel
        {
            public int ProductId { get; set; }
            public int CharacteristicID { get; set; }
        }

        [HttpGet("units")]
        public async Task<IActionResult> GetUnits()
        {
            var units = new List<Unit>();

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string query = "SELECT unitid, unitname FROM units ORDER BY unitname;";

            using var cmd = new NpgsqlCommand(query, connection);
            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                units.Add(new Unit
                {
                    UnitID = reader.GetInt32(0),
                    Name = reader.GetString(1)
                });
            }

            return Ok(units);
        }

        public class AddProductDto
        {
            public string Name { get; set; }
            public int CategoryID { get; set; }
            public int UnitID { get; set; }
            public List<int> CharacteristicIDs { get; set; } = new();
        }

        [HttpPost("products")]
        public async Task<IActionResult> AddProduct([FromBody] AddProductDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                return BadRequest("Название товара обязательно");

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var transaction = await connection.BeginTransactionAsync();

            try
            {
                // 1. Вставляем товар
                string insertProductQuery = @"
            INSERT INTO products (name, categoryid, unitid)
            VALUES (@name, @categoryid, @unitid)
            RETURNING productid;";

                int productId;

                using (var cmd = new NpgsqlCommand(insertProductQuery, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@name", dto.Name);
                    cmd.Parameters.AddWithValue("@categoryid", dto.CategoryID);
                    cmd.Parameters.AddWithValue("@unitid", dto.UnitID);

                    productId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }

                // 2. Привязываем характеристики
                foreach (var characteristicId in dto.CharacteristicIDs.Distinct())
                {
                    string insertCharQuery = @"
                INSERT INTO productcharacteristics (productid, characteristicid)
                VALUES (@productid, @characteristicid);";

                    using var cmd = new NpgsqlCommand(insertCharQuery, connection, transaction);
                    cmd.Parameters.AddWithValue("@productid", productId);
                    cmd.Parameters.AddWithValue("@characteristicid", characteristicId);
                    await cmd.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();

                return Ok(new { ProductID = productId });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, $"Ошибка при добавлении товара: {ex.Message}");
            }
        }


        [HttpPost("add-category")]
        public async Task<IActionResult> AddCategory([FromBody] Category dto)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = "INSERT INTO categories (name) VALUES (@Name) RETURNING categoryid;";
            using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@Name", dto.Name);

            var id = (int)await cmd.ExecuteScalarAsync();
            return Ok(new Category { CategoryID = id, Name = dto.Name });
        }

        [HttpPost("add-unit")]
        public async Task<IActionResult> AddUnit([FromBody] Unit dto)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = "INSERT INTO units (unitname) VALUES (@Name) RETURNING unitid;";
            using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@Name", dto.Name);

            var id = (int)await cmd.ExecuteScalarAsync();
            return Ok(new Unit { UnitID = id, Name = dto.Name });
        }

        public class ExportRequestDto
        {
            public int RequestId { get; set; }
            public int UserId { get; set; }       // ID пользователя для получения его сертификата

            public string FirstName { get; set; }      // имя
            public string MiddleName { get; set; }     // отчество
            public string LastName { get; set; }       // фамилия
            public string UserRole { get; set; }       // должность
                                                       // 👇 Добавляем для директора
        }


      

        // ... (весь остальной код остаётся без изменений)

        private string GenerateRequestConditionsHtml(
     RequestDetailsDto request,
     List<(string FullName, string Role, X509Certificate2 Certificate)> signatures)
        {
            var sb = new StringBuilder();

            // Таблица с товарами и характеристиками
            sb.AppendLine("<table border='1' cellspacing='0' cellpadding='5' style='width:100%; border-collapse:collapse;'>");
            sb.AppendLine("<tr style='background-color:#f0f0f0; font-weight:bold;'>");
            sb.AppendLine("<th>Товар</th><th>Характеристика</th><th>Значение</th><th>Ед. изм.</th><th>Кол-во</th><th>Цена</th>");
            sb.AppendLine("</tr>");

            foreach (var item in request.RequestItems)
            {
                var rowSpan = item.RequestItemCharacteristics?.Count > 0 ? item.RequestItemCharacteristics.Count : 1;

                for (int i = 0; i < rowSpan; i++)
                {
                    sb.AppendLine("<tr>");

                    if (i == 0)
                    {
                        sb.AppendLine($"<td rowspan='{rowSpan}'>{System.Net.WebUtility.HtmlEncode(item.ProductName)}</td>");
                    }

                    if (item.RequestItemCharacteristics?.Count > i)
                    {
                        var ch = item.RequestItemCharacteristics[i];
                        sb.AppendLine($"<td>{System.Net.WebUtility.HtmlEncode(ch.ProductCharacteristic?.CharacteristicName ?? "")}</td>");
                        sb.AppendLine($"<td>{System.Net.WebUtility.HtmlEncode(ch.ValueRequest)}</td>");
                    }
                    else
                    {
                        sb.AppendLine("<td></td><td></td>");
                    }

                    if (i == 0)
                    {
                        sb.AppendLine($"<td rowspan='{rowSpan}'>{System.Net.WebUtility.HtmlEncode(item.Unit?.UnitName ?? "")}</td>");
                        sb.AppendLine($"<td rowspan='{rowSpan}'>{item.Quantity}</td>");
                        sb.AppendLine($"<td rowspan='{rowSpan}'>{item.UnitPrice:0.00}</td>");
                    }

                    sb.AppendLine("</tr>");
                }
            }

            sb.AppendLine("</table>");
            var tableHtml = sb.ToString();

            // Блоки подписей
            var signatureBlocks = new StringBuilder();
            int sigIndex = 1;
            foreach (var (fullName, role, cert) in signatures)
            {
                var serialNumber = cert?.SerialNumber ?? "—";
                var validFrom = cert != null ? cert.NotBefore.ToString("dd.MM.yyyy") : "—";
                var validTo = cert != null ? cert.NotAfter.ToString("dd.MM.yyyy") : "—";
                var signedAt = DateTime.Now.ToString("dd.MM.yyyy HH:mm");

                signatureBlocks.AppendLine($@"
    <div class='signature-block'>
        <table class='signature-table'>
            <tr><td class='bold' style='width:35%;'>Подпись #{sigIndex}</td><td></td></tr>
            <tr><td class='bold'>Решение:</td><td>Подписан</td></tr>
            <tr><td class='bold'>Владелец:</td><td>{System.Net.WebUtility.HtmlEncode(fullName)}</td></tr>
            <tr><td class='bold'>Должность:</td><td>{System.Net.WebUtility.HtmlEncode(role)}</td></tr>
            <tr><td class='bold'>Дата подписания:</td><td>{signedAt} (МСК) (UTC+03:00)</td></tr>
            <tr><td class='bold'>Серийный номер сертификата:</td><td>{serialNumber}</td></tr>
            <tr><td class='bold'>Срок действия:</td><td>с {validFrom} по {validTo}</td></tr>
        </table>
    </div>");
                sigIndex++;
            }

            return $@"
<!DOCTYPE html>
<html lang='ru'>
<head>
    <meta charset='UTF-8'>
    <title>Лист подписания условий заявки</title>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 20px; line-height: 1.5; }}
        h1, h2 {{ font-size: 14pt; margin-bottom: 10px; }}
        .document-info {{ margin-bottom: 15px; }}
        .document-info div {{ margin-bottom: 5px; }}
        .section {{ margin-bottom: 15px; }}
        .bold {{ font-weight: bold; }}
        .signature-block {{ border: 1px solid #000; border-radius: 10px; padding: 8px; margin-top: 10px; overflow: hidden; }}
        .signature-table {{ width: 100%; border-collapse: collapse; font-size: 12pt; }}
        .signature-table td {{ border: none; padding: 6px 8px; vertical-align: top; }}
        .signature-block .signature-table {{ border: 1px solid #000; }}
        .signature-table tr:nth-child(even) {{ background-color: #e0e0e0; }}
        .signature-table tr:nth-child(2) td,
        .signature-table tr:nth-child(3) td {{ border-top: 2px solid #000; }}
    </style>
</head>

<body>
    <h1>Лист подписания условий заявки</h1>

    <div class='section'>
        <h2>Сведения о документе</h2>
        <div class='document-info'>
            <div>Наименование документа: Условия заявки</div>
            <div>Документ от: <span class='bold'>{request.RequestDate:dd.MM.yyyy} (МСК)</span></div>
            <div class='bold'>Предмет заявки:</div>
            {tableHtml}
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
        {signatureBlocks}
    </div>
</body>
</html>";
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
                _logger.LogError($"Ошибка подписания: {ex}");
                throw;
            }
        }






        string GetGenitiveRole(string role)
        {
            return role switch
            {
                "Директор" => "Директора",
                "Бухгалтер" => "Бухгалтера",
                "Менеджер" => "Менеджера",
                "Заведующий" => "Заведующего",
                _ => role // по умолчанию без изменений
            };
        }

        string GetGenitiveFullName(string fullName)
        {
            // Простейшее склонение — только для теста и демо
            var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) return fullName;

            string lastName = parts[0], firstName = parts[1], middleName = parts[2];

            // Примитивная логика: добавить окончание "а"/"ой"/"ы"/"евича" — замените по нужному
            string genitiveLast = lastName + "а";
            string genitiveFirst = firstName.EndsWith("а") ? firstName.TrimEnd('а') + "ы" : firstName + "а";
            string genitiveMiddle = middleName.EndsWith("ич") ? middleName + "а" : middleName + "ны";

            return $"{genitiveLast} {genitiveFirst} {genitiveMiddle}";
        }




        [HttpPost("ExportToWord")]
        public async Task<IActionResult> ExportToWord([FromBody] ExportRequestDto dto, [FromQuery] bool withSignature = false)
        {
            string fullName = $"{dto.LastName} {dto.FirstName} {dto.MiddleName}";
            string role = dto.UserRole;
            int? createdByUserId = null;
            int? completedByUserId = null;
            string delegatedFullName = null;
            string templatePhrase = null;
            string createdFullName = "", createdRole = "";
            string completedFullName = "", completedRole = "";
            DateTime completedDate = DateTime.MinValue;



            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            var signatures = await LoadAllSignatures(connection, dto.RequestId);
            // Найдём шаблон и пользователя, выбранных директором
            var directorSignature = signatures.FirstOrDefault(s => s.UserId == dto.UserId);
            if (directorSignature != null && directorSignature.DelegatedUserId.HasValue)
            {
                var nameQuery = @"
        SELECT u.lastname, u.firstname, u.middlename
        FROM users u
        WHERE u.userid = @id";
                using (var nameCmd = new NpgsqlCommand(nameQuery, connection))
                {
                    nameCmd.Parameters.AddWithValue("@id", directorSignature.DelegatedUserId.Value);
                    using var nameReader = await nameCmd.ExecuteReaderAsync();
                    if (await nameReader.ReadAsync())
                    {
                        delegatedFullName = $"{nameReader.GetString(0)} {nameReader.GetString(1)} {nameReader.GetString(2)}";
                    }
                }

                // ✅ Это работает как для шаблона, так и для ввода вручную
                if (!string.IsNullOrWhiteSpace(directorSignature.TemplateText))
                {
                    templatePhrase = directorSignature.TemplateText.Trim();
                }
            }



            var directorIds = new List<int>();
            using (var directorCmd = new NpgsqlCommand(
                "SELECT userid FROM users WHERE role_id = (SELECT roleid FROM role WHERE name = 'Директор')",
                connection))
            using (var reader = await directorCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    directorIds.Add(reader.GetInt32(0));
                }
            }
            // ✅ Только присваиваем, без повторного объявления
            // Получаем ID всех подписавших
           


            if (dto.RequestId <= 0)
                return BadRequest("Некорректный ID заявки");


            // Загрузка заявки
            var requestQuery = @"
    SELECT 
        r.requestid, 
        r.requestdate, 
        r.description, 
        r.signedbyuserid,
        r.createdbyuserid,
        r.completedbyuserid,
        r.completeddate
    FROM requests r
    WHERE r.requestid = @requestId;";

            RequestDetailsDto request = null;
            int? signerId = null;
            var participants = new List<int>();
            using (var cmd = new NpgsqlCommand(requestQuery, connection))
            {
                cmd.Parameters.AddWithValue("@requestId", dto.RequestId);
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    request = new RequestDetailsDto
                    {
                        RequestID = reader.GetInt32(0),
                        RequestDate = reader.GetDateTime(1),
                        Description = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        RequestItems = new List<RequestItemDto>()
                    };
                    signerId = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
                    createdByUserId = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);
                    completedByUserId = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
                    completedDate = reader.IsDBNull(6) ? DateTime.MinValue : reader.GetDateTime(6); // ✅ верная дата завершения

                    if (createdByUserId.HasValue) participants.Add(createdByUserId.Value);
                    if (completedByUserId.HasValue) participants.Add(completedByUserId.Value);
                }
            }
            if (createdByUserId.HasValue)
            {
                var creatorQuery = @"
        SELECT u.lastname, u.firstname, u.middlename, r.name
        FROM users u
        LEFT JOIN role r ON u.role_id = r.roleid
        WHERE u.userid = @uid";

                using var creatorCmd = new NpgsqlCommand(creatorQuery, connection);
                creatorCmd.Parameters.AddWithValue("@uid", createdByUserId.Value);

                using var creatorReader = await creatorCmd.ExecuteReaderAsync();
                if (await creatorReader.ReadAsync())
                {
                    createdFullName = $"{creatorReader.GetString(0)} {creatorReader.GetString(1)} {creatorReader.GetString(2)}";
                    createdRole = creatorReader.GetString(3);
                }
            }
            if (completedByUserId.HasValue)
            {
                var completedQuery = @"
        SELECT u.lastname, u.firstname, u.middlename, r.name
        FROM users u
        LEFT JOIN role r ON u.role_id = r.roleid
        WHERE u.userid = @uid";
                using var completedCmd = new NpgsqlCommand(completedQuery, connection);
                completedCmd.Parameters.AddWithValue("@uid", completedByUserId.Value);

                using var completedReader = await completedCmd.ExecuteReaderAsync();
                if (await completedReader.ReadAsync())
                {
                    completedFullName = $"{completedReader.GetString(0)} {completedReader.GetString(1)} {completedReader.GetString(2)}";
                    completedRole = completedReader.GetString(3);
                }
            }

            // Допустим, берём дату завершения заявки из request.RequestDate — если нужна отдельная таблица истории, скажи



            // Исключаем из директоров тех, кто уже подписал как участник
            var signedUserIds = signatures.Select(s => s.UserId).ToHashSet();

            // Исключаем из директоров тех, кто уже подписал как участник
            var effectiveDirectorIds = directorIds.Except(participants).ToList();

            bool allParticipantsSigned = participants.All(p => signedUserIds.Contains(p));
            bool allEffectiveDirectorsSigned = effectiveDirectorIds.All(d => signedUserIds.Contains(d));
            bool showPrintedDirectorSignature = !withSignature || (withSignature && (!allParticipantsSigned || !allEffectiveDirectorsSigned)); ;

            if (request == null)
                return NotFound("Заявка не найдена");

            // Загрузка товаров с характеристиками
            var itemQuery = @"
SELECT 
    ri.productid, 
    p.name AS productname, 
    u.unitid, 
    u.unitname, 
    ri.quantity, 
    ri.unitprice,
    pc.characteristicid, 
    ch.name AS characteristicname, 
    ric.valuerequest
FROM requestitems ri
JOIN products p ON ri.productid = p.productid
JOIN units u ON p.unitid = u.unitid
LEFT JOIN requestitemcharacteristics ric ON ric.requestitemid = ri.requestitemid
LEFT JOIN productcharacteristics pc ON ric.productcharacteristicid = pc.productcharacteristicid
LEFT JOIN characteristics ch ON pc.characteristicid = ch.characteristicid
WHERE ri.requestid = @requestId
ORDER BY ri.productid;";


            using (var cmd = new NpgsqlCommand(itemQuery, connection))
            {
                cmd.Parameters.AddWithValue("@requestId", dto.RequestId);

                using var reader = await cmd.ExecuteReaderAsync();

                var itemDict = new Dictionary<int, RequestItemDto>();

                while (await reader.ReadAsync())
                {
                    int productId = reader.GetInt32(0);

                    if (!itemDict.TryGetValue(productId, out var item))
                    {
                        item = new RequestItemDto
                        {
                            ProductID = productId,
                            ProductName = reader.GetString(1),
                            Unit = new UnitDto
                            {
                                UnitID = reader.GetInt32(2),
                                UnitName = reader.GetString(3)
                            },
                            Quantity = reader.GetInt32(4),
                            UnitPrice = reader.GetDecimal(5),
                            RequestItemCharacteristics = new List<RequestItemCharacteristicDto>()
                        };
                        itemDict[productId] = item;
                        request.RequestItems.Add(item);
                    }

                    if (!reader.IsDBNull(6))
                    {
                        item.RequestItemCharacteristics.Add(new RequestItemCharacteristicDto
                        {
                            ProductCharacteristic = new ProductCharacteristicDto
                            {
                                CharacteristicID = reader.GetInt32(6),
                                CharacteristicName = reader.GetString(7)
                            },
                            ValueRequest = reader.GetString(8)
                        });
                    }
                }
            }

            // Создание документа
            var doc = DocX.Create("Служебная_записка.docx");

            // Генерация QR-кода
            var qrGenerator = new QRCodeGenerator();
var link = $"https://request-contracts-client.onrender.com/create-contract?id={dto.RequestId}";
            var qrData = qrGenerator.CreateQrCode(link, QRCodeGenerator.ECCLevel.Q);
            var qrCode = new PngByteQRCode(qrData);
            byte[] qrBytes = qrCode.GetGraphic(20);

            using var qrStream = new MemoryStream(qrBytes);
            var image = doc.AddImage(qrStream);
            var picture = image.CreatePicture(80, 80);

            // Таблица с ID заявки и QR-кодом
            var topTable = doc.AddTable(2, 2);
            topTable.Alignment = Alignment.right;
            topTable.Design = TableDesign.None;

            if (!string.IsNullOrEmpty(delegatedFullName) && !string.IsNullOrEmpty(templatePhrase))
            {
                topTable.Rows[0].Cells[0].Paragraphs[0]
                    .Append($"{delegatedFullName} {templatePhrase}.")
                    .FontSize(10)
                    .Italic()
                    .SpacingAfter(8);
            }

            // Левая колонка — подпись директора (только если без подписи)
            if (showPrintedDirectorSignature)
            {
                topTable.Rows[0].Cells[0].Paragraphs[0]
                    .Append("Директор\nНенашева Олеся Александровна")
                    .FontSize(10)
                    .Bold()
                    .Alignment = Alignment.left;

                topTable.Rows[1].Cells[0].Paragraphs[0]
                    .Append("_____________")
                    .AppendLine()
                    .Append("«__» __________ 20__ г.")
                    .FontSize(10)
                    .Alignment = Alignment.left;
            }


            // Ячейка с номером заявки
            topTable.Rows[0].Cells[1].Paragraphs[0]
                .Append($"Номер заявки: {request.RequestID}")
                .FontSize(10)
                .Bold()
                .Alignment = Alignment.right;

            // Ячейка с QR-кодом
            topTable.Rows[1].Cells[1].Paragraphs[0]
                .AppendPicture(picture)
                .Alignment = Alignment.right;

            doc.InsertTable(topTable);


            // Фиксированные поля
            doc.InsertParagraph("Директору Ненашевой Олеси Александровне\nМБОУ образовательного комплекса им. В. Храброго")
                .Alignment = Alignment.right;

            var genitiveRole = GetGenitiveRole(createdRole);
            var genitiveName = GetGenitiveFullName(createdFullName);

            var fromWhom = doc.InsertParagraph($"От: {genitiveRole} {genitiveName}");
            fromWhom.Alignment = Alignment.right;
            fromWhom.SpacingAfter(20); ;

            var p2 = doc.InsertParagraph("СЛУЖЕБНАЯ ЗАПИСКА").FontSize(16).Bold();
            p2.Alignment = Alignment.center;
            p2.SpacingAfter(20);

            var p3 = doc.InsertParagraph(request.Description);
            p3.Alignment = Alignment.center;
            p3.SpacingAfter(30);


            var table = doc.AddTable(1, 6);
            table.Design = TableDesign.TableGrid;

            table.Rows[0].Cells[0].Paragraphs[0].Append("Товар").Bold();
            table.Rows[0].Cells[1].Paragraphs[0].Append("Характеристика").Bold();
            table.Rows[0].Cells[2].Paragraphs[0].Append("Значение").Bold();
            table.Rows[0].Cells[3].Paragraphs[0].Append("Ед. изм.").Bold();
            table.Rows[0].Cells[4].Paragraphs[0].Append("Кол-во").Bold();
            table.Rows[0].Cells[5].Paragraphs[0].Append("Цена").Bold();

            foreach (var item in request.RequestItems)
            {
                var rows = new List<Row>();

                for (int i = 0; i < item.RequestItemCharacteristics.Count; i++)
                {
                    var ch = item.RequestItemCharacteristics[i];
                    var row = table.InsertRow();
                    row.Cells[1].Paragraphs[0].Append(ch.ProductCharacteristic?.CharacteristicName ?? "");
                    row.Cells[2].Paragraphs[0].Append(ch.ValueRequest);

                    if (i == 0)
                    {
                        row.Cells[0].Paragraphs[0].Append(item.ProductName);
                        row.Cells[3].Paragraphs[0].Append(item.Unit?.UnitName ?? "");
                        row.Cells[4].Paragraphs[0].Append(item.Quantity.ToString());
                        row.Cells[5].Paragraphs[0].Append(item.UnitPrice > 0 ? item.UnitPrice.ToString("0.00") : "–");
                    }

                    rows.Add(row);
                }

                if (rows.Count > 1)
                {
                    table.MergeCellsInColumn(0, table.RowCount - rows.Count, table.RowCount - 1);
                    table.MergeCellsInColumn(3, table.RowCount - rows.Count, table.RowCount - 1);
                    table.MergeCellsInColumn(4, table.RowCount - rows.Count, table.RowCount - 1);
                    table.MergeCellsInColumn(5, table.RowCount - rows.Count, table.RowCount - 1);
                }
            }

            doc.InsertTable(table);
        

            if (!allParticipantsSigned || !allEffectiveDirectorsSigned)
            {
                var russianCulture = new CultureInfo("ru-RU");

                var signTable = doc.AddTable(2, 3);
                signTable.Alignment = Alignment.right;
                signTable.Design = TableDesign.None;
                signTable.SetWidths(new float[] { 220f, 150f, 200f });

                signTable.Rows[0].Cells[0].Paragraphs[0].Append(createdFullName).Alignment = Alignment.right;
                signTable.Rows[0].Cells[1].Paragraphs[0].Append("_____________").Alignment = Alignment.center;
                var requestDateText = $"«{request.RequestDate.ToString("dd", russianCulture)}» {request.RequestDate.ToString("MMMM yyyy", russianCulture)} года";

                signTable.Rows[0].Cells[2].Paragraphs[0]
                    .Append(requestDateText)
                    .FontSize(11)
                    .Alignment = Alignment.left;
                signTable.Rows[1].Cells[1].Paragraphs[0].Append("подпись").Alignment = Alignment.center;

                doc.InsertParagraph().SpacingBefore(10);
                doc.InsertTable(signTable);
                // Добавим техническую строку: кто утвердил
                if (!string.IsNullOrEmpty(completedFullName) && !string.IsNullOrEmpty(completedRole) && completedDate != DateTime.MinValue)
                {

                    var approvedTable = doc.AddTable(2, 3);
                    approvedTable.Alignment = Alignment.right;
                    approvedTable.Design = TableDesign.None;
                    approvedTable.SetWidths(new float[] { 220f, 150f, 200f });

                    // Первая строка
                    approvedTable.Rows[0].Cells[0].Paragraphs[0]
                        .Append($"Утверждено: {completedRole} {completedFullName}")
                        .FontSize(11)
                        .Alignment = Alignment.right;

                    approvedTable.Rows[0].Cells[1].Paragraphs[0]
                        .Append("_____________")
                        .FontSize(11)
                        .Alignment = Alignment.center;

                    var completedDateText = $"«{completedDate.ToString("dd", russianCulture)}» {completedDate.ToString("MMMM yyyy", russianCulture)} года";

                    approvedTable.Rows[0].Cells[2].Paragraphs[0]
                        .Append(completedDateText)
                        .FontSize(11)
                        .Alignment = Alignment.left;


                    var signPara = approvedTable.Rows[1].Cells[1].Paragraphs[0];
                    signPara.Append("подпись")
                        .FontSize(11)
                        .SpacingBefore(-15)  // меньшее значение — ближе к линии
                        .SpacingAfter(0)
                        .Alignment = Alignment.center;




                    doc.InsertParagraph().SpacingBefore(10);
                    doc.InsertTable(approvedTable);
                }





            }
            else if (allParticipantsSigned && allEffectiveDirectorsSigned)
            {
                var totalSignTable = doc.AddTable(2, signatures.Count);
                totalSignTable.Alignment = Alignment.center;
                totalSignTable.SetWidths(Enumerable.Repeat(230f, signatures.Count).ToArray());

                // ✅ Объединяем первую строку вручную
                if (signatures.Count > 1)
                {
                    totalSignTable.Rows[0].MergeCells(0, signatures.Count - 1);
                }
                var headerCell = totalSignTable.Rows[0].Cells[0];
                var headerParagraph = headerCell.Paragraphs[0];
                headerParagraph.Alignment = Alignment.center;
                headerParagraph.Append("ДОКУМЕНТ ПОДПИСАН\nЭЛЕКТРОННОЙ ПОДПИСЬЮ")
                    .Font("Arial")
                    .FontSize(12)
                    .Bold()
                    .Color(Color.DarkBlue)
                    .SpacingAfter(10);
                foreach (var row in totalSignTable.Rows)
                {
                    foreach (var cell in row.Cells)
                    {
                        cell.FillColor = Color.AliceBlue;
                    }
                }

                int colIndex = 0;

                foreach (var sig in signatures)
                {
                    var cert = LoadCertificate(sig.CertData, sig.KeyData, sig.Password, sig.CertType);

                    // Получаем владельца и роль
                    string name = "", roleName = "";
                    DateTime signedDate = DateTime.MinValue;

                    var signerQuery = @"
SELECT u.lastname, u.firstname, u.middlename, r.name, rs.SignedDate
FROM users u
LEFT JOIN role r ON u.role_id = r.roleid
LEFT JOIN RequestSignatures rs ON rs.UserId = u.UserId AND rs.RequestId = @requestId
WHERE u.userid = @id";
                    using var signerCmd = new NpgsqlCommand(signerQuery, connection);
                    signerCmd.Parameters.AddWithValue("@id", sig.UserId);
                    signerCmd.Parameters.AddWithValue("@requestId", dto.RequestId);

                    using var signerReader = await signerCmd.ExecuteReaderAsync();
                    if (await signerReader.ReadAsync())
                    {
                        name = $"{signerReader.GetString(0)} {signerReader.GetString(1)} {signerReader.GetString(2)}";
                        roleName = signerReader.GetString(3);
                        signedDate = signerReader.GetDateTime(4);
                    }

                    var cell = totalSignTable.Rows[1].Cells[colIndex++];
                    cell.FillColor = Color.AliceBlue;
                    cell.MarginTop = 16;
                    cell.MarginBottom = 16;

                    // Обводка
                    var border = new Border(BorderStyle.Tcbs_single, BorderSize.two, 0, Color.DarkBlue);
                    cell.SetBorder(TableCellBorderType.Top, border);
                    cell.SetBorder(TableCellBorderType.Bottom, border);
                    cell.SetBorder(TableCellBorderType.Left, border);
                    cell.SetBorder(TableCellBorderType.Right, border);

                    var p = cell.Paragraphs[0];
                    p.Alignment = Alignment.center;
                    p.AppendLine($"Сертификат: {cert.SerialNumber}")
                     .FontSize(10).Color(Color.Black);
                    p.AppendLine($"Должность: {roleName}")
                     .FontSize(10).Color(Color.Black);
                    p.AppendLine($"Владелец: {name}")
                     .FontSize(10).Color(Color.Black);
                    p.AppendLine($"Подписано: {signedDate:dd.MM.yyyy HH:mm}")
                     .FontSize(10).Color(Color.Black);
                    p.AppendLine($"Действителен с {cert.NotBefore:dd.MM.yyyy} по {cert.NotAfter:dd.MM.yyyy}")
                     .FontSize(10).Color(Color.Black);

                }

                doc.InsertParagraph().SpacingBefore(15);
                doc.InsertTable(totalSignTable);


            }

      
            doc.SaveAs("placeholder"); // генерируем в память ниже

            // Сохраняем документ в массив
            // Сохраняем документ в массив
            byte[] documentBytes;
            using (var ms = new MemoryStream())
            {
                doc.SaveAs(ms);
                documentBytes = ms.ToArray();
            }

          



            Console.WriteLine($"withSignature = {withSignature}");
            Console.WriteLine($"hasDirectorSigned = {allParticipantsSigned}");
            Console.WriteLine($"allParticipantsSigned = {allParticipantsSigned}");

            if (withSignature && (!allParticipantsSigned || !allEffectiveDirectorsSigned))
            {
                var missing = new List<string>();

                foreach (var p in participants)
                {
                    if (!signatures.Any(s => s.UserId == p))
                        missing.Add(await GetUserFullName(connection, p));
                }

                foreach (var did in directorIds.Except(participants))
                {
                    if (!signatures.Any(s => s.UserId == did))
                        missing.Add(await GetUserFullName(connection, did));
                }


                Response.Headers.Add("X-Missing-Signatures", JsonSerializer.Serialize(missing));
            }
            if (withSignature && allParticipantsSigned && allEffectiveDirectorsSigned)
            {
                // ✅ Упаковать в ZIP, как раньше
                using var zipStream = new MemoryStream();
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
                {
                    var entry = archive.CreateEntry("document.docx");
                    using (var entryStream = entry.Open())
                    {
                        entryStream.Write(documentBytes, 0, documentBytes.Length);
                    }

                    foreach (var sig in signatures)
                    {
                        var cert = LoadCertificate(sig.CertData, sig.KeyData, sig.Password, sig.CertType);
                        var signedData = SignDocumentWithPKCS7(documentBytes, cert.GetRSAPrivateKey(), cert);

                        var sigEntry = archive.CreateEntry($"signature_{sig.UserId}.p7s");
                        using var sigStream = sigEntry.Open();
                        sigStream.Write(signedData, 0, signedData.Length);
                    }

                    // HTML файл
                    var htmlEntry = archive.CreateEntry($"Условия_заявки_{request.RequestID}.html");
                    using (var htmlStream = new StreamWriter(htmlEntry.Open(), Encoding.UTF8))
                    {
                        var signatureInfos = new List<(string FullName, string Role, X509Certificate2 Certificate)>();

                        foreach (var sig in signatures)
                        {
                            var cert = LoadCertificate(sig.CertData, sig.KeyData, sig.Password, sig.CertType);

                            string name = "";
                            string userRole = "";

                            var signerQuery = @"
SELECT u.lastname, u.firstname, u.middlename, r.name
FROM users u
LEFT JOIN role r ON u.role_id = r.roleid
WHERE u.userid = @id";
                            using var signerCmd = new NpgsqlCommand(signerQuery, connection);
                            signerCmd.Parameters.AddWithValue("@id", sig.UserId);
                            using var signerReader = await signerCmd.ExecuteReaderAsync();
                            if (await signerReader.ReadAsync())
                            {
                                name = $"{signerReader.GetString(0)} {signerReader.GetString(1)} {signerReader.GetString(2)}";
                                userRole = signerReader.GetString(3);
                            }

                            signatureInfos.Add((name, userRole, cert));
                        }

                        var html = GenerateRequestConditionsHtml(request, signatureInfos);
                        await htmlStream.WriteAsync(html);
                    }
                }

                zipStream.Position = 0;
                return File(zipStream.ToArray(), "application/zip", $"Служебная_записка_{dto.RequestId}_signed.zip");
            }

            // ❌ Не все подписали — возвращаем обычный DOCX

            return File(documentBytes,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                $"Служебная_записка_{dto.RequestId}.docx");
        }


        
        private async Task<string> GetUserFullName(NpgsqlConnection conn, int userId)
        {
            var cmd = new NpgsqlCommand("SELECT lastname, firstname, middlename FROM users WHERE userid = @id", conn);
            cmd.Parameters.AddWithValue("@id", userId);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return $"{reader.GetString(0)} {reader.GetString(1)} {reader.GetString(2)}";
            }
            return "Неизвестно";
        }


        private X509Certificate2 LoadCertificate(byte[] certData, byte[] keyData, string password, string certType)
        {
            if (certType == "PFX")
            {
                return new X509Certificate2(certData, password,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            }
            else
            {
                var cert = new X509Certificate2(certData);
                using var rsa = RSA.Create();
                rsa.ImportEncryptedPkcs8PrivateKey(password, keyData, out _);
                return cert.CopyWithPrivateKey(rsa);
            }
        }
        private async Task<List<UserSignatureDto>> LoadAllSignatures(NpgsqlConnection connection, int requestId)
        {
            var signatures = new List<UserSignatureDto>();
            var query = @"
     SELECT 
    rs.userid, 
    uc.CertificateData, 
    uc.CertificateType, 
    uk.PrivateKeyData, 
    uk.KeyPassword,
    rs.DelegatedUserId,  
    rs.TemplateId,
    rs.TemplateText      
FROM RequestSignatures rs
JOIN Users u ON rs.userid = u.userid
LEFT JOIN UserCertificates uc ON u.Certificate_id = uc.UserCertificateID
LEFT JOIN UserPrivateKeys uk ON u.Private_key_id = uk.UserPrivateKeyID
WHERE rs.requestid = @requestId";

            using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@requestId", requestId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                signatures.Add(new UserSignatureDto
                {
                    UserId = reader.GetInt32(0),
                    CertData = reader.GetFieldValue<byte[]>(1),
                    CertType = reader.GetString(2),
                    KeyData = reader.GetFieldValue<byte[]>(3),
                    Password = reader.GetString(4),
                    DelegatedUserId = reader.IsDBNull(5) ? null : reader.GetInt32(5), // ✅
                    TemplateId = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    TemplateText = reader.IsDBNull(7) ? null : reader.GetString(7)

                });
            }
            return signatures;
        }

        public class UserSignatureDto
        {
            public int UserId { get; set; }
            public byte[] CertData { get; set; }
            public string CertType { get; set; }
            public byte[] KeyData { get; set; }
            public string Password { get; set; }

            public int? DelegatedUserId { get; set; }
            public int? TemplateId { get; set; }
            public string? TemplateText { get; set; } // 👈 добавить

        }



        [HttpGet("product/{id}")]
        public async Task<IActionResult> GetProductById(int id)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Получаем товар, его категорию и единицу измерения
            const string productQuery = @"
        SELECT 
            p.productid, p.name, 
            p.categoryid, c.name AS categoryname, 
            p.unitid, u.unitname
        FROM products p
        LEFT JOIN categories c ON p.categoryid = c.categoryid
        LEFT JOIN units u ON p.unitid = u.unitid
        WHERE p.productid = @id;
    ";

            using var productCmd = new NpgsqlCommand(productQuery, connection);
            productCmd.Parameters.AddWithValue("@id", id);

            using var reader = await productCmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
                return NotFound($"Товар с ID {id} не найден.");

            var product = new Products
            {
                ProductID = reader.GetInt32(0),
                Name = reader.GetString(1),
                CategoryID = reader.GetInt32(2),
                Category = new Category
                {
                    CategoryID = reader.GetInt32(2),
                    Name = reader.GetString(3)
                },
                UnitID = reader.GetInt32(4),
                Unit = new Unit
                {
                    UnitID = reader.GetInt32(4),
                    Name = reader.GetString(5)
                },
                ProductCharacteristics = new List<ProductCharacteristics>()
            };

            await reader.CloseAsync();

            // Получаем характеристики, связанные с товаром
            const string charQuery = @"
        SELECT 
            pc.productcharacteristicid,
            c.characteristicid,
            c.name
        FROM productcharacteristics pc
        JOIN characteristics c ON c.characteristicid = pc.characteristicid
        WHERE pc.productid = @id;
    ";

            using var charCmd = new NpgsqlCommand(charQuery, connection);
            charCmd.Parameters.AddWithValue("@id", id);

            using var charReader = await charCmd.ExecuteReaderAsync();

            while (await charReader.ReadAsync())
            {
                product.ProductCharacteristics.Add(new ProductCharacteristics
                {
                    ProductCharacteristicID = charReader.GetInt32(0),
                    Characteristic = new Models.Characteristics
                    {
                        CharacteristicID = charReader.GetInt32(1),
                        Name = charReader.GetString(2)
                    }
                });
            }

            return Ok(product);
        }



        [HttpPut("update-product")]
        public async Task<IActionResult> UpdateProduct([FromBody] ProductUpdateDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.Name) || dto.ProductID <= 0)
                return BadRequest("Некорректные данные");

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = await connection.BeginTransactionAsync();

            try
            {
                // Получаем старые данные товара
                const string oldDataQuery = @"
            SELECT p.name, c.name AS categoryname, u.unitname
            FROM products p
            LEFT JOIN categories c ON p.categoryid = c.categoryid
            LEFT JOIN units u ON p.unitid = u.unitid
            WHERE p.productid = @id;";

                string? oldProductName = null, oldCategoryName = null, oldUnitName = null;

                using (var getCmd = new NpgsqlCommand(oldDataQuery, connection, transaction))
                {
                    getCmd.Parameters.AddWithValue("@id", dto.ProductID);
                    using var reader = await getCmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        oldProductName = reader.GetString(0);
                        oldCategoryName = reader.GetString(1);
                        oldUnitName = reader.GetString(2);
                    }
                }

                // Обновляем только если изменилось название товара
                if (!string.Equals(oldProductName?.Trim(), dto.Name.Trim(), StringComparison.Ordinal))
                {
                    const string updateProduct = "UPDATE products SET name = @name WHERE productid = @id;";
                    using var cmd = new NpgsqlCommand(updateProduct, connection, transaction);
                    cmd.Parameters.AddWithValue("@name", dto.Name.Trim());
                    cmd.Parameters.AddWithValue("@id", dto.ProductID);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Обновляем категорию, если изменилось имя
                if (!string.Equals(oldCategoryName?.Trim(), dto.CategoryName.Trim(), StringComparison.Ordinal))
                {
                    const string updateCategory = "UPDATE categories SET name = @name WHERE categoryid = @id;";
                    using var cmd = new NpgsqlCommand(updateCategory, connection, transaction);
                    cmd.Parameters.AddWithValue("@name", dto.CategoryName.Trim());
                    cmd.Parameters.AddWithValue("@id", dto.CategoryID);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Обновляем единицу, если изменилось имя
                if (!string.Equals(oldUnitName?.Trim(), dto.UnitName.Trim(), StringComparison.Ordinal))
                {
                    const string updateUnit = "UPDATE units SET unitname = @name WHERE unitid = @id;";
                    using var cmd = new NpgsqlCommand(updateUnit, connection, transaction);
                    cmd.Parameters.AddWithValue("@name", dto.UnitName.Trim());
                    cmd.Parameters.AddWithValue("@id", dto.UnitID);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Получаем старые характеристики (id + имя)
                var oldCharMap = new Dictionary<int, string>();
                const string getOldChars = @"
            SELECT c.characteristicid, c.name
            FROM productcharacteristics pc
            JOIN characteristics c ON c.characteristicid = pc.characteristicid
            WHERE pc.productid = @pid;";

                using (var cmd = new NpgsqlCommand(getOldChars, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@pid", dto.ProductID);
                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                        oldCharMap[reader.GetInt32(0)] = reader.GetString(1);
                }

                // Получаем характеристики, которые используются в заявках
                var protectedCharIds = new HashSet<int>();
                const string getUsedChars = @"
            SELECT pc.characteristicid
            FROM productcharacteristics pc
            JOIN requestitemcharacteristics ric ON ric.productcharacteristicid = pc.productcharacteristicid
            WHERE pc.productid = @pid;";

                using (var cmd = new NpgsqlCommand(getUsedChars, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@pid", dto.ProductID);
                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                        protectedCharIds.Add(reader.GetInt32(0));
                }

                // Удаляем только те связи, которые не используются в заявках
                foreach (var oldCharId in oldCharMap.Keys)
                {
                    if (!protectedCharIds.Contains(oldCharId) &&
                        !dto.Characteristics.Any(c => c.CharacteristicID == oldCharId))
                    {
                        const string deleteLink = "DELETE FROM productcharacteristics WHERE productid = @pid AND characteristicid = @cid;";
                        using var delCmd = new NpgsqlCommand(deleteLink, connection, transaction);
                        delCmd.Parameters.AddWithValue("@pid", dto.ProductID);
                        delCmd.Parameters.AddWithValue("@cid", oldCharId);
                        await delCmd.ExecuteNonQueryAsync();
                    }
                }


                // Обновляем и связываем характеристики
                foreach (var ch in dto.Characteristics)
                {
                    if (oldCharMap.TryGetValue(ch.CharacteristicID, out var oldName))
                    {
                        if (!string.Equals(oldName?.Trim(), ch.Name.Trim(), StringComparison.Ordinal))
                        {
                            const string updateChar = "UPDATE characteristics SET name = @name WHERE characteristicid = @id;";
                            using var cmd = new NpgsqlCommand(updateChar, connection, transaction);
                            cmd.Parameters.AddWithValue("@name", ch.Name.Trim());
                            cmd.Parameters.AddWithValue("@id", ch.CharacteristicID);
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }

                    // Добавляем связь, если её не было
                    if (!oldCharMap.ContainsKey(ch.CharacteristicID))
                    {
                        const string insertLink = "INSERT INTO productcharacteristics (productid, characteristicid) VALUES (@pid, @cid);";
                        using var bindCmd = new NpgsqlCommand(insertLink, connection, transaction);
                        bindCmd.Parameters.AddWithValue("@pid", dto.ProductID);
                        bindCmd.Parameters.AddWithValue("@cid", ch.CharacteristicID);
                        await bindCmd.ExecuteNonQueryAsync();
                    }
                }

                await transaction.CommitAsync();
                return Ok(new { dto.ProductID });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, $"Ошибка при обновлении товара: {ex.Message}");
            }
        }


        public class ProductUpdateDto
        {
            public int ProductID { get; set; }
            public string Name { get; set; }

            public int CategoryID { get; set; }
            public string CategoryName { get; set; }

            public int UnitID { get; set; }
            public string UnitName { get; set; }

            public List<CharacteristicDto> Characteristics { get; set; }
        }

        public class CharacteristicDto
        {
            public int CharacteristicID { get; set; }
            public string Name { get; set; }
        }



        [HttpPut("{requestId}/update-items")]
        public async Task<IActionResult> UpdateRequestItemsAdo(int requestId, [FromBody] UpdateRequestItemsRequest request)
        {
            if (request == null)
                return BadRequest("Данные запроса отсутствуют.");

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = await connection.BeginTransactionAsync();


            try
            {

                if (!string.IsNullOrWhiteSpace(request.Description))
                {
                    const string updateDescriptionQuery = @"
            UPDATE requests
            SET description = @desc
            WHERE requestid = @requestId;
        ";

                    using (var descCmd = new NpgsqlCommand(updateDescriptionQuery, connection, transaction))
                    {
                        descCmd.Parameters.AddWithValue("@desc", request.Description);
                        descCmd.Parameters.AddWithValue("@requestId", requestId);
                        await descCmd.ExecuteNonQueryAsync();
                    }
                }

                // 1. Удаление товаров и их характеристик
                if (request.RemovedRequestItemIds?.Any() == true)
                {
                    foreach (var requestItemId in request.RemovedRequestItemIds)
                    {
                        // Удалить характеристики, связанные с этой строкой
                        const string deleteCharsQuery = @"
DELETE FROM requestitemcharacteristics
WHERE requestitemid = @requestItemId;";
                        using (var cmd = new NpgsqlCommand(deleteCharsQuery, connection, transaction))
                        {
                            cmd.Parameters.AddWithValue("@requestItemId", requestItemId);
                            await cmd.ExecuteNonQueryAsync();
                        }

                        // Удалить саму строку из requestitems
                        const string deleteItemQuery = @"
DELETE FROM requestitems 
WHERE requestitemid = @requestItemId;";
                        using (var cmd = new NpgsqlCommand(deleteItemQuery, connection, transaction))
                        {
                            cmd.Parameters.AddWithValue("@requestItemId", requestItemId);
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }
                }


                // 2. Обработка добавления или обновления товаров
                foreach (var dto in request.RequestItems)
                {
                    int requestItemId = dto.RequestItemID;

                    if (requestItemId == 0)
                    {
                        // Вставка новой строки
                        const string insertItemQuery = @"
INSERT INTO requestitems (requestid, productid, quantity, unitprice, totalprice)
VALUES (@requestId, @productId, @quantity, @unitPrice, @quantity * @unitPrice)
RETURNING requestitemid;";
                        using (var cmd = new NpgsqlCommand(insertItemQuery, connection, transaction))
                        {
                            cmd.Parameters.AddWithValue("@requestId", requestId);
                            cmd.Parameters.AddWithValue("@productId", dto.ProductID);
                            cmd.Parameters.AddWithValue("@quantity", dto.Quantity);
                            cmd.Parameters.AddWithValue("@unitPrice", dto.UnitPrice);
                            requestItemId = (int)(await cmd.ExecuteScalarAsync() ?? 0);
                        }
                    }
                    else
                    {
                        // Обновление
                        const string updateItemQuery = @"
UPDATE requestitems 
SET quantity = @quantity,
    unitprice = @unitPrice,
    totalprice = @quantity * @unitPrice
WHERE requestitemid = @requestItemId;";
                        using (var cmd = new NpgsqlCommand(updateItemQuery, connection, transaction))
                        {
                            cmd.Parameters.AddWithValue("@requestItemId", requestItemId);
                            cmd.Parameters.AddWithValue("@quantity", dto.Quantity);
                            cmd.Parameters.AddWithValue("@unitPrice", dto.UnitPrice);
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }

                    // Удаление характеристик
                    if (dto.RemovedCharacteristicIds?.Any() == true)
                    {
                        const string deleteCharQuery = @"
DELETE FROM requestitemcharacteristics
WHERE requestitemid = @requestItemId AND productcharacteristicid = ANY(@removedIds);";
                        using var cmd = new NpgsqlCommand(deleteCharQuery, connection, transaction);
                        cmd.Parameters.AddWithValue("@requestItemId", requestItemId);
                        cmd.Parameters.AddWithValue("@removedIds", dto.RemovedCharacteristicIds);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    // Обновление/вставка характеристик
                    foreach (var ch in dto.Characteristics)
                    {
                        const string updateCharQuery = @"
UPDATE requestitemcharacteristics 
SET valuerequest = @value 
WHERE requestitemid = @requestItemId AND productcharacteristicid = @charId;";
                        using (var updateCmd = new NpgsqlCommand(updateCharQuery, connection, transaction))
                        {
                            updateCmd.Parameters.AddWithValue("@value", ch.ValueRequest ?? "");
                            updateCmd.Parameters.AddWithValue("@requestItemId", requestItemId);
                            updateCmd.Parameters.AddWithValue("@charId", ch.ProductCharacteristicID);
                            var affected = await updateCmd.ExecuteNonQueryAsync();

                            if (affected == 0)
                            {
                                const string insertCharQuery = @"
INSERT INTO requestitemcharacteristics (requestitemid, productcharacteristicid, valuerequest)
VALUES (@requestItemId, @charId, @value);";
                                using var insertCmd = new NpgsqlCommand(insertCharQuery, connection, transaction);
                                insertCmd.Parameters.AddWithValue("@requestItemId", requestItemId);
                                insertCmd.Parameters.AddWithValue("@charId", ch.ProductCharacteristicID);
                                insertCmd.Parameters.AddWithValue("@value", ch.ValueRequest ?? "");
                                await insertCmd.ExecuteNonQueryAsync();
                            }
                        }
                    }
                }

                await transaction.CommitAsync();
                return Ok("Заявка успешно обновлена.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, "Ошибка при сохранении: " + ex.Message);
            }
        }





        public class UpdateRequestItemsRequest
        {
            public string? Description { get; set; } // ✅ Добавлено
            public List<UpdateRequestItemDto> RequestItems { get; set; } = new();
            public List<int> RemovedRequestItemIds { get; set; } = new(); // 🔄
        }


        public class UpdateRequestItemDto
        {
            public int RequestItemID { get; set; } // 👈 Добавляем
            public int ProductID { get; set; }
            public int Quantity { get; set; }
            public decimal UnitPrice { get; set; }
            public List<UpdateCharacteristicDto> Characteristics { get; set; } = new();
            public List<int> RemovedCharacteristicIds { get; set; } = new();

        }

        public class UpdateCharacteristicDto
        {
            public int ProductCharacteristicID { get; set; }
            public string ValueRequest { get; set; }
        }


        [HttpGet("history")]
        public async Task<IActionResult> GetRequestHistory()
        {
            var result = new List<RequestHistoryDto>();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string query = @"
SELECT 
    r.requestid,
    r.description,
    r.requestdate,
    u1.lastname || ' ' || u1.firstname || ' ' || COALESCE(u1.middlename, '') AS createdby,
    r1.name AS createdbyrole,
    r.completeddate,
    u2.lastname || ' ' || u2.firstname || ' ' || COALESCE(u2.middlename, '') AS completedby,
    r2.name AS completedbyrole,
    s.name AS statusname
FROM requests r
LEFT JOIN users u1 ON r.createdbyuserid = u1.userid
LEFT JOIN Role r1 ON u1.role_id = r1.RoleId
LEFT JOIN users u2 ON r.completedbyuserid = u2.userid
LEFT JOIN Role r2 ON u2.role_id = r2.RoleId
LEFT JOIN statuses s ON r.statusid = s.statusid
ORDER BY r.requestdate DESC;";




            using var cmd = new NpgsqlCommand(query, connection);
            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var dto = new RequestHistoryDto
                {
                    RequestID = reader.GetInt32(0),
                    Description = reader.IsDBNull(1) ? null : reader.GetString(1),
                    RequestDate = reader.GetDateTime(2),
                    CreatedBy = reader.IsDBNull(3) ? "-" : reader.GetString(3),
                    CreatedByRole = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CompletedDate = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                    CompletedBy = reader.IsDBNull(6) ? null : reader.GetString(6),
                    CompletedByRole = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Status = reader.IsDBNull(8) ? "-" : reader.GetString(8)
                };


                result.Add(dto);
            }

            return Ok(result);
        }

        public class RequestHistoryDto
        {
            public int RequestID { get; set; }
            public string Description { get; set; }
            public DateTime RequestDate { get; set; }
            public string CreatedBy { get; set; }
            public string CreatedByRole { get; set; }
            public DateTime? CompletedDate { get; set; }
            public string CompletedBy { get; set; }
            public string CompletedByRole { get; set; }
            public string Status { get; set; }
        }

        public class ArchiveRequestDto
        {
            public int RequestID { get; set; }
            public string FileName { get; set; }
            public byte[] FileData { get; set; } // Бинарные данные файла
            public int UserID { get; set; }
        }

        [HttpPost("SaveArchive")]
        public async Task<IActionResult> SaveArchive([FromBody] ArchiveRequestDto archiveRequest)
        {
            try
            {
                var query = @"
          INSERT INTO requestarchives (requestid, filename, filedata, userid, archivedate)
VALUES (@RequestID, @FileName, @FileData, @UserID, @ArchiveDate)
ON CONFLICT (requestid)
DO UPDATE SET 
    filename = EXCLUDED.filename,
    filedata = EXCLUDED.filedata,
    userid = EXCLUDED.userid,
    archivedate = EXCLUDED.archivedate;
";

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                using var cmd = new NpgsqlCommand(query, connection);
                cmd.Parameters.AddWithValue("@RequestID", archiveRequest.RequestID);
                cmd.Parameters.AddWithValue("@FileName", archiveRequest.FileName);
                cmd.Parameters.AddWithValue("@FileData", archiveRequest.FileData);
                cmd.Parameters.AddWithValue("@UserID", archiveRequest.UserID);
                cmd.Parameters.AddWithValue("@ArchiveDate", DateTime.UtcNow);

                var result = await cmd.ExecuteNonQueryAsync();

                return result > 0
                    ? Ok("Файл обновлён или добавлен в базу данных.")
                    : BadRequest("Не удалось сохранить файл.");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Ошибка: {ex.Message}");
            }
        }

        public class ArchiveRequestDto1
        {
            public int ArchiveID { get; set; }
            public int RequestID { get; set; }
            public string FileName { get; set; }
            public byte[] FileData { get; set; }
            public DateTime ArchiveDate { get; set; }
            public int UserID { get; set; }
            public string UserFullName { get; set; } // Изменено с UserName на полное ФИО
        }

        [HttpGet("GetArchives")]
        public async Task<IActionResult> GetArchives()
        {
            try
            {
                var result = new List<ArchiveRequestDto1>();

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                const string query = @"
        SELECT 
            a.ArchiveID,
            a.RequestID,
            a.FileName,
            a.FileData,
            a.ArchiveDate,
            a.UserID,
            u.LastName,
            u.FirstName,
            u.MiddleName
        FROM RequestArchives a
        LEFT JOIN Users u ON a.UserID = u.UserID
        ORDER BY a.ArchiveDate DESC;";

                using var cmd = new NpgsqlCommand(query, connection);
                using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    var archiveDto = new ArchiveRequestDto1
                    {
                        ArchiveID = reader.GetInt32(0),
                        RequestID = reader.GetInt32(1),
                        FileName = reader.GetString(2),
                        FileData = reader.IsDBNull(3) ? null : (byte[])reader[3],
                        ArchiveDate = reader.GetDateTime(4),
                        UserID = reader.GetInt32(5),
                        UserFullName = FormatFullName(
                            reader.IsDBNull(6) ? null : reader.GetString(6),
                            reader.IsDBNull(7) ? null : reader.GetString(7),
                            reader.IsDBNull(8) ? null : reader.GetString(8))
                    };

                    result.Add(archiveDto);
                }

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Ошибка при получении архивов: {ex.Message}");
            }
        }

        // Метод для форматирования полного имени
        private string FormatFullName(string lastName, string firstName, string middleName)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(lastName)) parts.Add(lastName);
            if (!string.IsNullOrWhiteSpace(firstName)) parts.Add(firstName);
            if (!string.IsNullOrWhiteSpace(middleName)) parts.Add(middleName);

            return parts.Count > 0 ? string.Join(" ", parts) : "Неизвестно";
        }

        [HttpGet("GetUserFullName/{userId}")]
        public async Task<ActionResult<string>> GetUserFullName(int userId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                const string query = @"
            SELECT LastName, FirstName, MiddleName 
            FROM Users 
            WHERE UserID = @UserId";

                using var cmd = new NpgsqlCommand(query, connection);
                cmd.Parameters.AddWithValue("@UserId", userId);

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    return FormatFullName(
                        reader.IsDBNull(0) ? null : reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2));
                }

                return "Неизвестно";
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Ошибка при получении ФИО пользователя: {ex.Message}");
            }
        }
        // DTO для подписи с делегированием
        public class SignRequestDto
        {
            public int UserId { get; set; }             // Кто подписывает
            public int? DelegatedUserId { get; set; }
            public int? RequestTemplateId { get; set; } // ✅
            public string? CustomTemplateText { get; set; } // 👈 добавили

        }

        [HttpPost("{id}/sign")]
        public async Task<IActionResult> SignRequest(int id, [FromBody] SignRequestDto dto)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Проверка: уже подписал?
            var checkCmd = new NpgsqlCommand("SELECT 1 FROM requestsignatures WHERE requestid = @reqId AND userid = @userId", connection);
            checkCmd.Parameters.AddWithValue("@reqId", id);
            checkCmd.Parameters.AddWithValue("@userId", dto.UserId);
            if (await checkCmd.ExecuteScalarAsync() is not null)
                return BadRequest("Вы уже подписали эту заявку.");

            // Проверка сертификата и ключа
            var certQuery = @"
SELECT c.certificatedata, c.certificatetype, k.privatekeydata, k.keypassword
FROM users u
LEFT JOIN usercertificates c ON u.certificate_id = c.usercertificateid
LEFT JOIN userprivatekeys k ON u.private_key_id = k.userprivatekeyid
WHERE u.userid = @userId";

            byte[]? certData = null, keyData = null;
            string? certType = null, keyPassword = null;

            using (var cmd = new NpgsqlCommand(certQuery, connection))
            {
                cmd.Parameters.AddWithValue("@userId", dto.UserId);
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    certData = reader.IsDBNull(0) ? null : (byte[])reader[0];
                    certType = reader.IsDBNull(1) ? null : (string)reader[1];
                    keyData = reader.IsDBNull(2) ? null : (byte[])reader[2];
                    keyPassword = reader.IsDBNull(3) ? null : (string)reader[3];
                }
            }

            if (certData == null || keyData == null)
            {
                var generate = await GenerateUserCertificate(dto.UserId);
                if (!generate.Success) return BadRequest("Ошибка генерации сертификата");

                // Повторная загрузка
                using var reloadCmd = new NpgsqlCommand(certQuery, connection);
                reloadCmd.Parameters.AddWithValue("@userId", dto.UserId);
                using var reloadReader = await reloadCmd.ExecuteReaderAsync();
                if (await reloadReader.ReadAsync())
                {
                    certData = reloadReader.GetFieldValue<byte[]>(0);
                    certType = reloadReader.GetString(1);
                    keyData = reloadReader.GetFieldValue<byte[]>(2);
                    keyPassword = reloadReader.GetString(3);
                }
            }

            // Проверка срока действия
            var cert = new X509Certificate2(certData, keyPassword,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            if (cert.NotAfter < DateTime.UtcNow)
                return BadRequest("Срок действия сертификата истек.");

            var rsa = cert.GetRSAPrivateKey() ?? throw new Exception("Нет приватного ключа");
            var signed = SignDocumentWithPKCS7(new byte[] { 0x01 }, rsa, cert);
            if (!ValidateDigitalSignature(signed, dto.UserId))
                return BadRequest("Подпись недействительна.");

            string? templateText = null;

            if (!string.IsNullOrWhiteSpace(dto.CustomTemplateText))
            {
                templateText = dto.CustomTemplateText.Trim();
            }
            else if (dto.RequestTemplateId.HasValue)
            {
                // Загрузка текста шаблона из БД
                using var tmplCmd = new NpgsqlCommand("SELECT text FROM requesttemplates WHERE templateid = @id", connection);
                tmplCmd.Parameters.AddWithValue("@id", dto.RequestTemplateId.Value);
                var result = await tmplCmd.ExecuteScalarAsync();
                templateText = result?.ToString();
            }

            // Вставка подписи
            var insertCmd = new NpgsqlCommand(@"
INSERT INTO requestsignatures 
(requestid, userid, signeddate, certificatedata, certificatetype, privatekeydata, keypassword, delegateduserid, templateid, templatetext)
VALUES 
(@reqId, @userId, NOW(), @cert, @certType, @key, @keyPass, @delegatedUserId, @templateId, @templateText)", connection);

            insertCmd.Parameters.AddWithValue("@reqId", id);
            insertCmd.Parameters.AddWithValue("@userId", dto.UserId);
            insertCmd.Parameters.AddWithValue("@cert", certData);
            insertCmd.Parameters.AddWithValue("@certType", certType ?? "PFX");
            insertCmd.Parameters.AddWithValue("@key", keyData);
            insertCmd.Parameters.AddWithValue("@keyPass", keyPassword ?? "");
            insertCmd.Parameters.AddWithValue("@delegatedUserId", (object?)dto.DelegatedUserId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@templateId", (object?)dto.RequestTemplateId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@templateText", (object?)templateText ?? DBNull.Value);


            await insertCmd.ExecuteNonQueryAsync();
            await NotifyDirectorIfReady(id); // <-- здесь


            return Ok(new { Message = "Подпись добавлена." });
        }

        [HttpGet("allUsers")]
        public async Task<IActionResult> GetAllUsers()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var result = new List<object>();
            var cmd = new NpgsqlCommand("SELECT userid, lastname, firstname, middlename, r.name FROM users u LEFT JOIN role r ON u.role_id = r.roleid", connection);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new
                {
                    Id = reader.GetInt32(0),
                    FullName = $"{reader.GetString(1)} {reader.GetString(2)} {reader.GetString(3)}",
                    Role = reader.IsDBNull(4) ? null : reader.GetString(4)
                });
            }
            return Ok(result);
        }


        [HttpGet("allTemplates")]
        public async Task<IActionResult> GetAllTemplates()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var result = new List<object>();
            var cmd = new NpgsqlCommand("SELECT templateid, text FROM requesttemplates", connection);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new
                {
                    RequestTemplateId = reader.GetInt32(0),  // 👈 Переименовано
                    TemplateText = reader.GetString(1)       // 👈 Переименовано
                });
            }
            return Ok(result);
        }


        [HttpPost("product-request/batch")]
        public async Task<IActionResult> SubmitMultipleProductRequests([FromBody] List<ProductRequestDto> requests)
        {
            if (requests == null || !requests.Any())
                return BadRequest("Список товаров пуст.");

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var userId = requests.First().UserId;

                // 1. Создаём группу
                var groupCmd = new NpgsqlCommand(@"
            INSERT INTO ProductRequestGroups (UserID, RequestDate)
            VALUES (@UserId, NOW())
            RETURNING ProductRequestGroupID;", connection, transaction);
                groupCmd.Parameters.AddWithValue("@UserId", userId);

                var groupId = (int)(await groupCmd.ExecuteScalarAsync() ?? throw new Exception("Не удалось создать группу"));

                // 2. Добавляем каждый товар в группу
                foreach (var request in requests)
                {
                    if (string.IsNullOrWhiteSpace(request.ProductName))
                        continue;

                    var cmd = new NpgsqlCommand(@"
                INSERT INTO ProductRequests 
                (GroupID, ProductName, CategoryName, Description, StatusID)
                VALUES 
                (@GroupId, @ProductName, @CategoryName, @Description, 1);", connection, transaction);

                    cmd.Parameters.AddWithValue("@GroupId", groupId);
                    cmd.Parameters.AddWithValue("@ProductName", request.ProductName);
                    cmd.Parameters.AddWithValue("@CategoryName", (object?)request.CategoryName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Description", (object?)request.Description ?? DBNull.Value);

                    await cmd.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
                return Ok(new { message = "Групповой запрос добавлен", groupId });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Ошибка при сохранении группового запроса на товары");
                return StatusCode(500, "Ошибка при сохранении");
            }
        }

        public class ProductRequestDto
        {
            public int UserId { get; set; }
            public string ProductName { get; set; }
            public string? CategoryName { get; set; }
            public string? Description { get; set; }
        }

        [HttpGet("product-requests")]
        public async Task<IActionResult> GetAllProductRequests()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
        SELECT g.ProductRequestGroupID, g.RequestDate,
               u.UserID, u.LastName, u.FirstName, u.MiddleName, u.Email,
               pr.ProductRequestID, pr.ProductName, pr.CategoryName, pr.Description,
               pr.StatusID, s.StatusName,
               pr.ProcessedByUserID, up.LastName, up.FirstName, up.MiddleName,
               pr.ProcessedDate, pr.LinkedRequestID
        FROM ProductRequestGroups g
        JOIN Users u ON g.UserID = u.UserID
        JOIN ProductRequests pr ON g.ProductRequestGroupID = pr.GroupID
        LEFT JOIN ProductRequestStatuses s ON pr.StatusID = s.StatusID
        LEFT JOIN Users up ON pr.ProcessedByUserID = up.UserID
        ORDER BY g.RequestDate DESC, pr.ProductRequestID ASC";

            var groups = new Dictionary<int, ProductRequestGroupDto>();

            using var cmd = new NpgsqlCommand(query, connection);
            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var groupId = reader.GetInt32(0);

                if (!groups.ContainsKey(groupId))
                {
                    groups[groupId] = new ProductRequestGroupDto
                    {
                        GroupId = groupId,
                        RequestDate = reader.GetDateTime(1),
                        CreatedBy = new UserModel
                        {
                            Id = reader.GetInt32(2),
                            FullName = $"{reader.GetString(3)} {reader.GetString(4)} {reader.GetString(5)}"
                        },
                        Email = reader.IsDBNull(6) ? "" : reader.GetString(6),
                        Requests = new List<ProductRequestItemDto>()
                    };
                }

                groups[groupId].Requests.Add(new ProductRequestItemDto
                {
                    ProductRequestID = reader.GetInt32(7),
                    ProductName = reader.GetString(8),
                    CategoryName = reader.IsDBNull(9) ? null : reader.GetString(9),
                    Description = reader.IsDBNull(10) ? null : reader.GetString(10),
                    StatusID = reader.GetInt32(11),
                    StatusName = reader.GetString(12),
                    ProcessedBy = reader.IsDBNull(13) ? null : new UserModel
                    {
                        Id = reader.GetInt32(13),
                        FullName = $"{reader.GetString(14)} {reader.GetString(15)} {reader.GetString(16)}"
                    },
                    ProcessedDate = reader.IsDBNull(17) ? null : reader.GetDateTime(17),
                    LinkedRequestID = reader.IsDBNull(18) ? null : reader.GetInt32(18)
                });
            }

            return Ok(groups.Values);
        }

        // DTO
        public class ProductRequestGroupDto
        {
            public int GroupId { get; set; }
            public DateTime RequestDate { get; set; }
            public UserModel CreatedBy { get; set; }
            public string Email { get; set; }
            public List<ProductRequestItemDto> Requests { get; set; }
        }

        public class ProductRequestItemDto
        {
            public int ProductRequestID { get; set; }
            public string ProductName { get; set; }
            public string? CategoryName { get; set; }
            public string? Description { get; set; }
            public int StatusID { get; set; }
            public string StatusName { get; set; }
            public UserModel? ProcessedBy { get; set; }
            public DateTime? ProcessedDate { get; set; }
            public int? LinkedRequestID { get; set; }
        }

        public class UserModel
        {
            public int Id { get; set; }  // Было UserID — стало Id
            public string FullName { get; set; }
        }


        [HttpDelete("reject-product-request-group/{groupId}")]
        public async Task<IActionResult> RejectProductRequestGroup(int groupId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                // Получаем email и ФИО пользователя
                var getUserCmd = new NpgsqlCommand(@"
       SELECT u.email, u.lastname, u.firstname, u.middlename
FROM productrequests pr
JOIN users u ON pr.groupid = u.userid
WHERE pr.groupid = @GroupId
            LIMIT 1", connection, transaction);
                getUserCmd.Parameters.AddWithValue("@GroupId", groupId);

                string? email = null;
                string fullName = "Пользователь";

                await using (var reader = await getUserCmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        email = reader.IsDBNull(0) ? null : reader.GetString(0);
                        var ln = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        var fn = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        var mn = reader.IsDBNull(3) ? "" : reader.GetString(3);
                        fullName = $"{ln} {fn} {mn}".Trim();
                    }
                }

                if (string.IsNullOrWhiteSpace(email))
                    return NotFound("Email пользователя не найден.");

                // Получаем ID статуса "Отклонён"
                var statusCmd = new NpgsqlCommand("SELECT statusid FROM productrequeststatuses WHERE statusname = 'Отклонён'", connection, transaction);
                var statusId = (int?)await statusCmd.ExecuteScalarAsync();
                if (statusId == null)
                    return BadRequest("Статус 'Отклонён' не найден.");

                // Обновляем статус всех заявок группы
                var updateCmd = new NpgsqlCommand(@"
            UPDATE productrequests
SET statusid = @StatusId, processeddate = NOW()
WHERE groupid = @GroupId", connection, transaction);
                updateCmd.Parameters.AddWithValue("@StatusId", statusId.Value);
                updateCmd.Parameters.AddWithValue("@GroupId", groupId);
                await updateCmd.ExecuteNonQueryAsync();

                // Отправка письма
                // Получить товары группы
                var rejectedItems = new List<ProductRequestItemDto>();

                var itemsCmd = new NpgsqlCommand(@"
    SELECT ProductName, CategoryName, Description
    FROM productrequests
    WHERE groupid = @GroupId", connection, transaction);
                itemsCmd.Parameters.AddWithValue("@GroupId", groupId);

                await using (var reader = await itemsCmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        rejectedItems.Add(new ProductRequestItemDto
                        {
                            ProductName = reader.GetString(0),
                            CategoryName = reader.IsDBNull(1) ? null : reader.GetString(1),
                            Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                        });
                    }
                }

                // Отправка письма со списком товаров
                await SendRejectionEmail(email, fullName, rejectedItems);

                await transaction.CommitAsync();
                return Ok(new { message = "Запросы отклонены и письмо отправлено." });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Ошибка при отклонении группы ProductRequests");
                return StatusCode(500, "Ошибка при отклонении запросов");
            }
        }
        private async Task SendRejectionEmail(string email, string fullName, List<ProductRequestItemDto> rejectedItems)
        {
            var config = _configuration.GetSection("Email");
            var fromEmail = config["FromEmail"];
            var fromName = config["FromName"];

            var subject = "Ваш запрос на добавление товара отклонён";

            var bodyBuilder = new StringBuilder();
            bodyBuilder.AppendLine($"<h2>Уважаемый(ая) {fullName},</h2>");
            bodyBuilder.AppendLine("<p>Ваш запрос на добавление следующих товаров был <strong>отклонён</strong>:</p>");
            bodyBuilder.AppendLine("<ul>");

            foreach (var item in rejectedItems)
            {
                bodyBuilder.AppendLine($"<li><strong>{item.ProductName}</strong>");
                if (!string.IsNullOrWhiteSpace(item.CategoryName))
                    bodyBuilder.AppendLine($" — Категория: {item.CategoryName}");
                if (!string.IsNullOrWhiteSpace(item.Description))
                    bodyBuilder.AppendLine($"<br/><em>{item.Description}</em>");
                bodyBuilder.AppendLine("</li>");
            }

            bodyBuilder.AppendLine("</ul>");
            bodyBuilder.AppendLine("<p>Если у вас есть вопросы, пожалуйста, свяжитесь с поддержкой или подайте запрос повторно.</p>");
            bodyBuilder.AppendLine($"<p>С уважением,<br/><strong>{fromName}</strong></p>");

            var message = new MailMessage
            {
                From = new MailAddress(fromEmail, fromName),
                Subject = subject,
                Body = bodyBuilder.ToString(),
                IsBodyHtml = true
            };
            message.To.Add(email);

            var smtpClient = new SmtpClient
            {
                Host = config["SmtpHost"],
                Port = int.Parse(config["SmtpPort"] ?? "587"),
                EnableSsl = true,
                Credentials = new System.Net.NetworkCredential(config["Username"], config["Password"])
            };

            await smtpClient.SendMailAsync(message);
        }



        [HttpGet("simple-users")]
        public async Task<IActionResult> GetSimpleUsers()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var result = new List<UserModel1>();
            var cmd = new NpgsqlCommand(@"
        SELECT userid, lastname, firstname, middlename 
        FROM users
    ", connection);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetInt32(0);
                var fullName = $"{reader.GetString(1)} {reader.GetString(2)} {reader.GetString(3)}";

                result.Add(new UserModel1
                {
                    Id = id,
                    FullName = fullName
                });
            }

            return Ok(result);
        }

        public class UserModel1
        {
            public int Id { get; set; }
            public string FullName { get; set; }
        }
        [HttpPut("approve-product-request-group/{groupId}")]
        public async Task<IActionResult> ApproveProductRequestGroup(int groupId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var updateCmd = new NpgsqlCommand(@"
        UPDATE productrequestgroups
        SET isapproved = TRUE
        WHERE productrequestgroupid = @GroupId", connection);
            updateCmd.Parameters.AddWithValue("@GroupId", groupId);

            var affected = await updateCmd.ExecuteNonQueryAsync();
            if (affected == 0)
                return NotFound("Группа не найдена.");

            return Ok(new { message = "Группа помечена как одобренная. Ожидается добавление товаров." });
        }
        [HttpPut("finalize-product-request-group/{groupId}")]
        public async Task<IActionResult> FinalizeProductRequestGroup(int groupId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                // Получаем email и ФИО
                var getUserCmd = new NpgsqlCommand(@"
            SELECT u.email, u.lastname, u.firstname, u.middlename
            FROM productrequestgroups g
            JOIN users u ON g.userid = u.userid
            WHERE g.productrequestgroupid = @GroupId", connection, transaction);
                getUserCmd.Parameters.AddWithValue("@GroupId", groupId);

                string? email = null;
                string fullName = "Пользователь";

                await using (var reader = await getUserCmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        email = reader.IsDBNull(0) ? null : reader.GetString(0);
                        var ln = reader.GetString(1);
                        var fn = reader.GetString(2);
                        var mn = reader.GetString(3);
                        fullName = $"{ln} {fn} {mn}".Trim();
                    }
                }

                if (string.IsNullOrWhiteSpace(email))
                    return NotFound("Email пользователя не найден.");

                // Получаем ID статуса "Принят"
                var statusCmd = new NpgsqlCommand("SELECT statusid FROM productrequeststatuses WHERE statusname = 'Принят'", connection, transaction);
                var statusId = (int?)await statusCmd.ExecuteScalarAsync();
                if (statusId == null)
                    return BadRequest("Статус 'Принят' не найден.");

                // Обновляем статус всех заявок группы
                var updateCmd = new NpgsqlCommand(@"
            UPDATE productrequests
            SET statusid = @StatusId, processeddate = NOW()
            WHERE groupid = @GroupId", connection, transaction);
                updateCmd.Parameters.AddWithValue("@StatusId", statusId.Value);
                updateCmd.Parameters.AddWithValue("@GroupId", groupId);
                await updateCmd.ExecuteNonQueryAsync();

                // Получаем товары
                var acceptedItems = new List<ProductRequestItemDto>();
                var itemsCmd = new NpgsqlCommand(@"
            SELECT ProductName, CategoryName, Description
            FROM productrequests
            WHERE groupid = @GroupId", connection, transaction);
                itemsCmd.Parameters.AddWithValue("@GroupId", groupId);

                await using (var reader = await itemsCmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        acceptedItems.Add(new ProductRequestItemDto
                        {
                            ProductName = reader.GetString(0),
                            CategoryName = reader.IsDBNull(1) ? null : reader.GetString(1),
                            Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                        });
                    }
                }

                await SendAcceptanceEmail(email, fullName, acceptedItems);

                await transaction.CommitAsync();
                return Ok(new { message = "Статус обновлён, письмо отправлено." });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Ошибка при финализации ProductRequestGroup");
                return StatusCode(500, "Ошибка при финализации.");
            }
        }



        private async Task SendAcceptanceEmail(string email, string fullName, List<ProductRequestItemDto> acceptedItems)
        {
            var config = _configuration.GetSection("Email");
            var fromEmail = config["FromEmail"];
            var fromName = config["FromName"];

            var subject = "Ваш запрос на добавление товара принят";

            var bodyBuilder = new StringBuilder();
            bodyBuilder.AppendLine($"<h2>Уважаемый(ая) {fullName},</h2>");
            bodyBuilder.AppendLine("<p>Ваш запрос на добавление следующих товаров был <strong>принят</strong>:</p>");
            bodyBuilder.AppendLine("<ul>");

            foreach (var item in acceptedItems)
            {
                bodyBuilder.AppendLine($"<li><strong>{item.ProductName}</strong>");
                if (!string.IsNullOrWhiteSpace(item.CategoryName))
                    bodyBuilder.AppendLine($" — Категория: {item.CategoryName}");
                if (!string.IsNullOrWhiteSpace(item.Description))
                    bodyBuilder.AppendLine($"<br/><em>{item.Description}</em>");
                bodyBuilder.AppendLine("</li>");
            }

            bodyBuilder.AppendLine("</ul>");
            bodyBuilder.AppendLine("<p>Товары будут рассмотрены и добавлены в систему в ближайшее время.</p>");
            bodyBuilder.AppendLine($"<p>С уважением,<br/><strong>{fromName}</strong></p>");

            var message = new MailMessage
            {
                From = new MailAddress(fromEmail, fromName),
                Subject = subject,
                Body = bodyBuilder.ToString(),
                IsBodyHtml = true
            };
            message.To.Add(email);

            var smtpClient = new SmtpClient
            {
                Host = config["SmtpHost"],
                Port = int.Parse(config["SmtpPort"] ?? "587"),
                EnableSsl = true,
                Credentials = new System.Net.NetworkCredential(config["Username"], config["Password"])
            };

            await smtpClient.SendMailAsync(message);
        }

        [HttpGet("product-request-items/{groupId}")]
        public async Task<IActionResult> GetProductRequestItems(int groupId)
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var items = new List<ProductRequestItemDto>();
            var cmd = new NpgsqlCommand("SELECT productname, categoryname, description FROM productrequests WHERE groupid = @groupId", conn);
            cmd.Parameters.AddWithValue("@groupId", groupId);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                items.Add(new ProductRequestItemDto
                {
                    ProductName = reader.GetString(0),
                    CategoryName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                });
            }

            return Ok(items);
        }



        [HttpPost("add-group-products")]
        public async Task<IActionResult> AddGroupProducts([FromBody] GroupProductAddRequest dto)
        {
            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            using var tx = await conn.BeginTransactionAsync();

            try
            {
                foreach (var product in dto.Products)
                {
                    // 1. Добавляем товар
                    var insertProductCmd = new NpgsqlCommand(@"
                INSERT INTO products (name, categoryid, unitid)
                VALUES (@Name, @CategoryId, @UnitId)
                RETURNING productid", conn, tx);

                    insertProductCmd.Parameters.AddWithValue("@Name", product.Name);
                    insertProductCmd.Parameters.AddWithValue("@CategoryId", product.CategoryID);
                    insertProductCmd.Parameters.AddWithValue("@UnitId", product.UnitID);

                    var productId = (int)await insertProductCmd.ExecuteScalarAsync();

                    // 2. Привязка характеристик (новых или существующих)
                    if (product.NewCharacteristics?.Any() == true)
                    {
                        foreach (var charName in product.NewCharacteristics.Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            // Поиск существующей характеристики
                            var selectCmd = new NpgsqlCommand("SELECT characteristicid FROM characteristics WHERE LOWER(name) = LOWER(@Name)", conn, tx);
                            selectCmd.Parameters.AddWithValue("@Name", charName);
                            var existingCharIdObj = await selectCmd.ExecuteScalarAsync();

                            int charId;

                            if (existingCharIdObj != null)
                            {
                                // Уже есть в БД
                                charId = (int)existingCharIdObj;
                            }
                            else
                            {
                                // Нет — создаём
                                var insertCharCmd = new NpgsqlCommand("INSERT INTO characteristics (name) VALUES (@Name) RETURNING characteristicid", conn, tx);
                                insertCharCmd.Parameters.AddWithValue("@Name", charName);
                                charId = (int)await insertCharCmd.ExecuteScalarAsync();
                            }

                            // Привязка к товару
                            var bindCmd = new NpgsqlCommand("INSERT INTO productcharacteristics (productid, characteristicid) VALUES (@ProductId, @CharacteristicId)", conn, tx);
                            bindCmd.Parameters.AddWithValue("@ProductId", productId);
                            bindCmd.Parameters.AddWithValue("@CharacteristicId", charId);
                            await bindCmd.ExecuteNonQueryAsync();
                        }
                    }

                    if (product.ExistingCharacteristicIDs?.Any() == true)
                    {
                        foreach (var id in product.ExistingCharacteristicIDs.Distinct())
                        {
                            var bindCmd = new NpgsqlCommand("INSERT INTO productcharacteristics (productid, characteristicid) VALUES (@ProductId, @CharacteristicId)", conn, tx);
                            bindCmd.Parameters.AddWithValue("@ProductId", productId);
                            bindCmd.Parameters.AddWithValue("@CharacteristicId", id);
                            await bindCmd.ExecuteNonQueryAsync();
                        }
                    }
                }

                // 3. Финализация группы
                var finalize = new HttpClient(); // Лучше сделать через DI, но сейчас сойдёт
                var finalizeRequest = new HttpRequestMessage(HttpMethod.Put, $"https://localhost:7120/api/Requests/finalize-product-request-group/{dto.GroupId}");
                await finalize.SendAsync(finalizeRequest);

                await tx.CommitAsync();
                return Ok(new { message = "Группа добавлена и финализирована" });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return StatusCode(500, "Ошибка при добавлении товаров: " + ex.Message);
            }
        }


        public class GroupProductAddRequest
        {
            public int GroupId { get; set; }
            public List<NewProductDto> Products { get; set; }
        }

        public class NewProductDto
        {
            public string Name { get; set; }
            public int CategoryID { get; set; }
            public int UnitID { get; set; }
            public List<string>? NewCharacteristics { get; set; }
            public List<int>? ExistingCharacteristicIDs { get; set; }
        }

        private async Task NotifyPendingSignatures(int requestId)
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            string? email = null, fullName = "Сотрудник", roleName = "пользователь", description = "";
            int reqId = 0;

            // Получение информации о заявке и создателе
            await using (var cmd = new NpgsqlCommand(@"
SELECT r.requestid, r.description,
       u.email, u.lastname || ' ' || u.firstname || ' ' || COALESCE(u.middlename, '') AS fullname,
       r1.name AS rolename
FROM requests r
LEFT JOIN users u ON r.createdbyuserid = u.userid
LEFT JOIN role r1 ON u.role_id = r1.roleid
WHERE r.requestid = @RequestId
LIMIT 1", conn))
            {
                cmd.Parameters.AddWithValue("@RequestId", requestId);

                await using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        reqId = reader.GetInt32(0);
                        description = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        email = reader.IsDBNull(2) ? null : reader.GetString(2);
                        fullName = reader.IsDBNull(3) ? "Сотрудник" : reader.GetString(3);
                        roleName = reader.IsDBNull(4) ? "пользователь" : reader.GetString(4);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(email))
                return;

            // Получаем товары из заявки
            var items = new List<string>();
            await using (var itemCmd = new NpgsqlCommand(@"
SELECT p.name, ri.quantity, u.unitname
FROM requestitems ri
JOIN products p ON ri.productid = p.productid
JOIN units u ON p.unitid = u.unitid
WHERE ri.requestid = @RequestId", conn))
            {
                itemCmd.Parameters.AddWithValue("@RequestId", requestId);
                await using var itemReader = await itemCmd.ExecuteReaderAsync();
                while (await itemReader.ReadAsync())
                {
                    var name = itemReader.GetString(0);
                    var quantity = itemReader.GetInt32(1);
                    var unit = itemReader.GetString(2);
                    items.Add($"- {name} ({quantity} {unit})");
                }
            }

            // Составляем тело письма
            var subject = $"Заявка №{reqId} ожидает вашей подписи";

            var body = $@"
<p>Уважаемый(ая) {fullName},</p>
<p>Вы, как <strong>{roleName}</strong>, создали заявку <strong>№{reqId}</strong> со следующим описанием:</p>
<blockquote>{description}</blockquote>
<p><strong>Список товаров:</strong></p>
<ul>{string.Join("", items.Select(i => $"<li>{i}</li>"))}</ul>
<p>Заявка завершена и ожидает вашей подписи.</p>
<p>Пожалуйста, перейдите в систему для её подтверждения.</p>
<p>С уважением,<br/>Автоматическая система</p>";

            var config = _configuration.GetSection("Email");

            var smtp = new SmtpClient
            {
                Host = config["SmtpHost"],
                Port = int.Parse(config["SmtpPort"] ?? "587"),
                EnableSsl = true,
                Credentials = new System.Net.NetworkCredential(config["Username"], config["Password"])
            };

            var message = new MailMessage
            {
                From = new MailAddress(config["FromEmail"], config["FromName"]),
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };

            message.To.Add(email);
            await smtp.SendMailAsync(message);
        }



        private async Task NotifyDirectorIfReady(int requestId)
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // Получаем ID всех, кто должен подписать
            var requiredUserIds = new List<int>();

            var getUserIdsCmd = new NpgsqlCommand(@"
        SELECT createdbyuserid, completedbyuserid
        FROM requests
        WHERE requestid = @RequestId", conn);
            getUserIdsCmd.Parameters.AddWithValue("@RequestId", requestId);

            using (var reader = await getUserIdsCmd.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    if (!reader.IsDBNull(0)) requiredUserIds.Add(reader.GetInt32(0));
                    if (!reader.IsDBNull(1)) requiredUserIds.Add(reader.GetInt32(1));
                }
            }

            // Получаем список уже подписавших
            var signedUserIds = new HashSet<int>();
            var signedCmd = new NpgsqlCommand(@"
        SELECT userid
        FROM requestsignatures
        WHERE requestid = @RequestId", conn);
            signedCmd.Parameters.AddWithValue("@RequestId", requestId);

            using (var reader = await signedCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    signedUserIds.Add(reader.GetInt32(0));
            }

            // Если не все обязательные подписали — выходим
            if (!requiredUserIds.All(signedUserIds.Contains))
                return;

            // Получаем директора
            var directorCmd = new NpgsqlCommand(@"
        SELECT u.email, u.lastname || ' ' || u.firstname || ' ' || COALESCE(u.middlename, '') AS fullname
        FROM users u
        JOIN role r ON u.role_id = r.roleid
        WHERE r.name = 'Директор'
        LIMIT 1", conn);

            string? directorEmail = null, directorName = null;
            using (var reader = await directorCmd.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    directorEmail = reader.IsDBNull(0) ? null : reader.GetString(0);
                    directorName = reader.IsDBNull(1) ? "Директор" : reader.GetString(1);
                }
            }

            if (string.IsNullOrWhiteSpace(directorEmail))
                return;

            // Получаем описание заявки
            var getDescCmd = new NpgsqlCommand("SELECT description FROM requests WHERE requestid = @RequestId", conn);
            getDescCmd.Parameters.AddWithValue("@RequestId", requestId);
            var description = (string?)await getDescCmd.ExecuteScalarAsync() ?? "";

            // Получаем список товаров
            var items = new List<string>();
            var itemsCmd = new NpgsqlCommand(@"
        SELECT p.name, ri.quantity, u.unitname
        FROM requestitems ri
        JOIN products p ON ri.productid = p.productid
        JOIN units u ON p.unitid = u.unitid
        WHERE ri.requestid = @RequestId", conn);
            itemsCmd.Parameters.AddWithValue("@RequestId", requestId);

            using (var itemReader = await itemsCmd.ExecuteReaderAsync())
            {
                while (await itemReader.ReadAsync())
                {
                    var name = itemReader.GetString(0);
                    var quantity = itemReader.GetInt32(1);
                    var unit = itemReader.GetString(2);
                    items.Add($"<li>{name} ({quantity} {unit})</li>");
                }
            }

            var itemListHtml = items.Any()
                ? $"<ul>{string.Join("", items)}</ul>"
                : "<p><em>Нет указанных товаров.</em></p>";

            // Составляем письмо
            var subject = $"Заявка №{requestId} ожидает вашей подписи";
            var body = $@"
<p>Уважаемый(ая) {directorName},</p>
<p>Все участники подписали заявку <strong>№{requestId}</strong>. Осталась только ваша подпись.</p>
<p><strong>Описание:</strong></p>
<blockquote>{description}</blockquote>
<p><strong>Список товаров:</strong></p>
{itemListHtml}
<p>Пожалуйста, перейдите в систему и подтвердите заявку.</p>
<p>С уважением,<br/>Автоматическая система</p>";

            // Отправка письма
            var config = _configuration.GetSection("Email");
            var smtp = new SmtpClient
            {
                Host = config["SmtpHost"],
                Port = int.Parse(config["SmtpPort"] ?? "587"),
                EnableSsl = true,
                Credentials = new System.Net.NetworkCredential(config["Username"], config["Password"])
            };

            var message = new MailMessage
            {
                From = new MailAddress(config["FromEmail"], config["FromName"]),
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };

            message.To.Add(directorEmail);
            await smtp.SendMailAsync(message);
        }


        [HttpPut("{id}/statusReq")]
        public async Task<IActionResult> UpdateRequestStatus(int id, [FromBody] UpdateStatusDto dto)
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand("UPDATE requests SET statusid = @StatusId WHERE requestid = @RequestId", conn);
            cmd.Parameters.AddWithValue("@StatusId", dto.StatusID);
            cmd.Parameters.AddWithValue("@RequestId", id);

            var rowsAffected = await cmd.ExecuteNonQueryAsync();

            if (rowsAffected == 0)
                return NotFound("Заявка не найдена");

            return Ok(new { message = "Статус заявки обновлён." });
        }

        public class UpdateStatusDto
        {
            public int StatusID { get; set; }
        }

    }




} 


