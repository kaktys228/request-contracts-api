using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using BCrypt.Net;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Authorization;
using System.Text.Json;
using System.Net.Http.Headers;

namespace APIdIplom.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AutoresationController : ControllerBase
    {
        public class JwtSettings
        {
            public string Secret { get; set; }
            public int ExpiryDays { get; set; }
        }
        private readonly string _connectionString;
        private readonly JwtSettings _jwtSettings;

        public AutoresationController(IConfiguration configuration, IOptions<JwtSettings> jwtSettings)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
            _jwtSettings = jwtSettings.Value;
        }

        public class LoginDto
        {
            public string Username { get; set; }
            public string Password { get; set; }
        }
        public class LoginResponse
        {
            public string Token { get; set; }
            public string Role { get; set; }
            public DateTime LastLoginDate { get; set; }
            public string LastName { get; set; }
            public string FirstName { get; set; }
            public string MiddleName { get; set; }
            public int UserId { get; set; }  // Добавьте это свойство
            public string Username { get; set; } // ← ДОБАВЬ ЭТУ СТРОКУ
            public bool EnforcePasswordExpiry { get; set; } // по аналогии, если возвращаешь его



        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto loginDto)
        {
            try
            {
                const string query = @"
SELECT 
    u.userid, 
    u.passwordhash, 
    u.passwordchangeddate, 
    u.enforce_password_expiry,
    r.name AS role,
    u.lastname,
    u.firstname,
    u.middlename
FROM users u
JOIN role r ON u.role_id = r.roleid
WHERE u.username = @Username";

                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("Username", loginDto.Username);

                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var storedPasswordHash = reader.GetString(reader.GetOrdinal("passwordhash"));
                    var passwordChangedDate = reader.GetDateTime(reader.GetOrdinal("passwordchangeddate"));
                    var enforcePasswordExpiry = !reader.IsDBNull(reader.GetOrdinal("enforce_password_expiry")) &&
                                                reader.GetBoolean(reader.GetOrdinal("enforce_password_expiry"));
                    var role = reader.GetString(reader.GetOrdinal("role"));
                    var lastName = reader.GetString(reader.GetOrdinal("lastname"));
                    var firstName = reader.GetString(reader.GetOrdinal("firstname"));
                    var middleName = reader.IsDBNull(reader.GetOrdinal("middlename"))
                        ? string.Empty
                        : reader.GetString(reader.GetOrdinal("middlename"));
                    var userId = reader.GetInt32(reader.GetOrdinal("userid"));  // Получаем UserID


                    if (enforcePasswordExpiry && (DateTime.Now - passwordChangedDate).TotalDays > 7)
                    {
                        return Unauthorized("Пароль истёк. Пожалуйста, смените пароль.");
                    }

                    if (BCrypt.Net.BCrypt.Verify(loginDto.Password, storedPasswordHash))
                    {
                        var tokenHandler = new JwtSecurityTokenHandler();
                        var key = Encoding.ASCII.GetBytes(_jwtSettings.Secret);

                        var tokenDescriptor = new SecurityTokenDescriptor
                        {
                            Subject = new ClaimsIdentity(new[]
                            {
                        new Claim(ClaimTypes.Name, loginDto.Username),
                        new Claim(ClaimTypes.Role, role),
                        new Claim("LastName", lastName),
                        new Claim("FirstName", firstName),
                        new Claim("MiddleName", middleName),
                        new Claim("UserId", userId.ToString())  // Добавляем UserID в токен

                    }),
                            Expires = DateTime.UtcNow.AddDays(_jwtSettings.ExpiryDays),
                            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
                        };

                        var token = tokenHandler.CreateToken(tokenDescriptor);
                        var tokenString = tokenHandler.WriteToken(token);

                        return Ok(new LoginResponse
                        {
                            Token = tokenString,
                            Role = role,
                            LastLoginDate = DateTime.Now,
                            LastName = lastName,
                            FirstName = firstName,
                            MiddleName = middleName,
                            UserId = userId  // Добавляем UserID в ответ

                        });
                    }

                    return Unauthorized("Неверный логин или пароль");
                }

                return Unauthorized("Неверный логин или пароль");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Ошибка сервера: {ex.Message}");
            }
        }


        [HttpGet("validateToken")]
        public async Task<IActionResult> ValidateToken()
        {
            try
            {
                var token = Request.Headers["Authorization"].ToString().Replace("Bearer ", "");

                if (string.IsNullOrEmpty(token))
                {
                    return Unauthorized("Токен не предоставлен");
                }

                var tokenHandler = new JwtSecurityTokenHandler();
                var key = Encoding.ASCII.GetBytes(_jwtSettings.Secret);

                ClaimsPrincipal principal;

                try
                {
                    principal = tokenHandler.ValidateToken(token, new TokenValidationParameters
                    {
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateLifetime = true,
                        IssuerSigningKey = new SymmetricSecurityKey(key)
                    }, out var validatedToken);
                }
                catch
                {
                    return Unauthorized("Токен недействителен");
                }

                var userIdClaim = principal.Claims.FirstOrDefault(c =>
                    c.Type == "UserId" || c.Type == "UserID");

                if (userIdClaim == null)
                {
                    return Unauthorized("User ID не найден в токене");
                }

                var userId = int.Parse(userIdClaim.Value);

                using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();

                var cmd = new NpgsqlCommand(@"
            SELECT passwordchangeddate, enforce_password_expiry 
            FROM users 
            WHERE userid = @id", conn);
                cmd.Parameters.AddWithValue("id", userId);

                using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    var changedDate = reader.GetDateTime(0);
                    var enforce = reader.GetBoolean(1);

                    if (enforce && (DateTime.UtcNow - changedDate).TotalDays > 7)
                    {
                        return Unauthorized("Пароль истёк — требуется повторный вход");
                    }

                    return Ok(new { Message = "Токен действителен" });
                }

                return Unauthorized("Пользователь не найден");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Ошибка сервера: {ex.Message}");
            }
        }


        public class ChangePasswordDto
        {
            public string Username { get; set; }
            public string OldPassword { get; set; }
            public string NewPassword { get; set; }
        }
        [HttpPost("changePassword")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto changePasswordDto)
        {
            try
            {
                const string query = @"
            SELECT u.passwordhash, u.passwordchangeddate 
            FROM users u 
            WHERE u.username = @Username";

                using (var connection = new NpgsqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("Username", changePasswordDto.Username);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                var storedPasswordHash = reader.GetString(reader.GetOrdinal("passwordhash"));
                                var passwordChangedDate = reader.GetDateTime(reader.GetOrdinal("passwordchangeddate"));

                                // Проверка старого пароля
                                if (BCrypt.Net.BCrypt.Verify(changePasswordDto.OldPassword, storedPasswordHash))
                                {
                                    // Закрываем reader перед выполнением нового запроса
                                    await reader.CloseAsync();

                                    // Хешируем новый пароль
                                    var newHashedPassword = BCrypt.Net.BCrypt.HashPassword(changePasswordDto.NewPassword);

                                    // Обновляем пароль и дату последнего входа
                                    const string updatePasswordQuery = @"
                                UPDATE users 
                                SET passwordhash = @NewPassword, passwordchangeddate = @PasswordChangedDate
                                WHERE username = @Username";

                                    using (var updateCommand = new NpgsqlCommand(updatePasswordQuery, connection))
                                    {
                                        updateCommand.Parameters.AddWithValue("NewPassword", newHashedPassword);
                                        updateCommand.Parameters.AddWithValue("PasswordChangedDate", DateTime.Now);
                                        updateCommand.Parameters.AddWithValue("Username", changePasswordDto.Username);

                                        await updateCommand.ExecuteNonQueryAsync();
                                    }

                                    return Ok("Пароль успешно изменён.");
                                }
                                else
                                {
                                    return Unauthorized("Неверный старый пароль.");
                                }
                            }
                            else
                            {
                                return Unauthorized("Пользователь не найден.");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Ошибка сервера: {ex.Message}");
            }
        }

        [HttpPut("toggle-password-expiry")]
        public async Task<IActionResult> TogglePasswordExpiry([FromBody] ToggleExpiryDto dto)
        {
            const string query = "UPDATE users SET enforce_password_expiry = @value WHERE username = @username";

            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = new NpgsqlCommand(query, conn);
            cmd.Parameters.AddWithValue("value", dto.Enforce);
            cmd.Parameters.AddWithValue("username", dto.Username);

            var affected = await cmd.ExecuteNonQueryAsync();
            return affected > 0 ? Ok("Настройка обновлена") : NotFound("Пользователь не найден");
        }

        public class ToggleExpiryDto
        {
            public string Username { get; set; }
            public bool Enforce { get; set; }
        }
        [HttpGet("get-password-expiry")]
        public async Task<IActionResult> GetPasswordExpirySetting([FromQuery] string username)
        {
            const string query = "SELECT enforce_password_expiry FROM users WHERE username = @username";

            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = new NpgsqlCommand(query, conn);
            cmd.Parameters.AddWithValue("username", username);

            var result = await cmd.ExecuteScalarAsync();

            if (result != null && result is bool flag)
                return Ok(flag);

            return NotFound("Пользователь не найден");
        }

        public class ActivityLogDto
        {
            public string Username { get; set; }
            public string IpAddress { get; set; }
            public string UserAgent { get; set; }
        }

        public class ActivityLogDto1
        {
            public string Username { get; set; }
        }

        public class LoginAttemptDto
        {
            public string Username { get; set; }
            public bool IsSuccess { get; set; }
            public string IpAddress { get; set; }
            public string UserAgent { get; set; }
        }


        [HttpPost("log-login")]
        public async Task<IActionResult> LogLogin([FromBody] ActivityLogDto dto)
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // Получение user_id
            await using var cmd = new NpgsqlCommand("SELECT userid FROM users WHERE username = @username", conn);
            cmd.Parameters.AddWithValue("username", dto.Username);
            var userId = await cmd.ExecuteScalarAsync();
            if (userId == null) return NotFound("Пользователь не найден");

            // Вставка в user_activity_log
            await using var insertCmd = new NpgsqlCommand(
                @"INSERT INTO user_activity_log (user_id, login_time, ip_address, user_agent)
              VALUES (@userId, NOW(), @ip, @agent)", conn);

            insertCmd.Parameters.AddWithValue("userId", (int)userId);
            insertCmd.Parameters.AddWithValue("ip", dto.IpAddress ?? "");
            insertCmd.Parameters.AddWithValue("agent", dto.UserAgent ?? "");

            await insertCmd.ExecuteNonQueryAsync();
            return Ok();
        }

        [HttpPost("log-logout")]
        public async Task<IActionResult> LogLogout([FromBody] ActivityLogDto1 dto)
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // Получение user_id
            await using var cmd = new NpgsqlCommand("SELECT userid FROM users WHERE username = @username", conn);
            cmd.Parameters.AddWithValue("username", dto.Username);

            var userIdObj = await cmd.ExecuteScalarAsync();
            if (userIdObj == null) return NotFound("Пользователь не найден");

            var userId = (int)userIdObj;

            // ✅ Правильный способ через CTE
            var updateQuery = @"
        WITH latest_session AS (
            SELECT id
            FROM user_activity_log
            WHERE user_id = @userId AND logout_time IS NULL
            ORDER BY login_time DESC
            LIMIT 1
        )
        UPDATE user_activity_log
        SET logout_time = NOW()
        WHERE id IN (SELECT id FROM latest_session);";

            await using var updateCmd = new NpgsqlCommand(updateQuery, conn);
            updateCmd.Parameters.AddWithValue("userId", userId);

            var rowsAffected = await updateCmd.ExecuteNonQueryAsync();
            if (rowsAffected == 0)
                return NotFound("Нет активной сессии для завершения");

            return Ok();
        }



        [HttpPost("log-attempt")]
        public async Task<IActionResult> LogAttempt([FromBody] LoginAttemptDto dto)
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var insertCmd = new NpgsqlCommand(@"
            INSERT INTO user_login_attempts (username, is_success, ip_address, user_agent, attempt_time)
            VALUES (@username, @isSuccess, @ip, @agent, NOW())", conn);

            insertCmd.Parameters.AddWithValue("username", dto.Username ?? "");
            insertCmd.Parameters.AddWithValue("isSuccess", dto.IsSuccess);
            insertCmd.Parameters.AddWithValue("ip", dto.IpAddress ?? "");
            insertCmd.Parameters.AddWithValue("agent", dto.UserAgent ?? "");

            await insertCmd.ExecuteNonQueryAsync();
            return Ok();
        }

        public class FaceLoginRequest
        {
            public string Base64Image { get; set; }
        }
        private readonly string _faceApiKey = "rx0NOXvLBBoz58ZXM1yrTnmt4wKnukfw";
        private readonly string _faceApiSecret = "Gl-fs20Bzy0bK4C-jjKrvYrhLmt2Vkxh"; // ← обязательно!


        
        [AllowAnonymous]
        [HttpPost("login-by-face")]
        public async Task<IActionResult> LoginByFace([FromBody] FaceLoginRequest request)
        {
            try
            {
                Console.WriteLine("🔍 Попытка входа по лицу...");
                byte[] uploadedImageBytes = Convert.FromBase64String(request.Base64Image);
                Console.WriteLine($"📸 Размер загруженного изображения: {uploadedImageBytes.Length} байт");

                using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();

                using var cmd = new NpgsqlCommand("SELECT u.userid, u.username, u.role_id, r.name AS role,\r\n       u.lastname, u.firstname, u.middlename, u.mainimage\r\nFROM users u\r\nJOIN role r ON u.role_id = r.roleid\r\nWHERE u.mainimage IS NOT NULL\r\n", conn);
                using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    var userId = reader.GetInt32(reader.GetOrdinal("userid"));
                    var username = reader.GetString(reader.GetOrdinal("username"));
                    var role = reader.GetString(reader.GetOrdinal("role")); // <-- новое поле
                    var lastName = reader.GetString(reader.GetOrdinal("lastname"));
                    var firstName = reader.GetString(reader.GetOrdinal("firstname"));
                    var middleName = reader.GetString(reader.GetOrdinal("middlename"));
                    var mainImageBytes = (byte[])reader["mainimage"];

                    Console.WriteLine($"👤 Сравнение с пользователем: {username} (ID: {userId})");

                    if (await CompareFaces(uploadedImageBytes, mainImageBytes))
                    {
                        Console.WriteLine($"✅ Лицо распознано как: {username}");

                        var tokenHandler = new JwtSecurityTokenHandler();
                        var key = Encoding.ASCII.GetBytes(_jwtSettings.Secret);

                        var tokenDescriptor = new SecurityTokenDescriptor
                        {
                            Subject = new ClaimsIdentity(new[]
{
    new Claim(ClaimTypes.Name, username),
    new Claim(ClaimTypes.Role, role),
    new Claim("UserId", userId.ToString()),
    new Claim("LastName", lastName),
    new Claim("FirstName", firstName),
    new Claim("MiddleName", middleName)
}),

                            Expires = DateTime.UtcNow.AddHours(3),
                            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
                        };

                        var token = tokenHandler.CreateToken(tokenDescriptor);
                        var tokenString = tokenHandler.WriteToken(token);

                        return Ok(new LoginResponse
                        {
                            Token = tokenString,
                            UserId = userId,
                            Username = username,
                            Role = role,
                            LastName = lastName,
                            FirstName = firstName,
                            MiddleName = middleName,
                            LastLoginDate = DateTime.Now,
                            EnforcePasswordExpiry = true // можно также подтянуть из базы
                        });

                    }
                    else
                    {
                        Console.WriteLine($"❌ Не совпадает с {username}");
                    }
                }

                Console.WriteLine("🚫 Ни одно лицо не совпало.");
                return Unauthorized("Лицо не распознано");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"🔥 Ошибка в LoginByFace: {ex}");
                return StatusCode(500, $"Ошибка сервера: {ex.Message}");
            }
        }
        private async Task<bool> CheckForGesture(string base64Image)
        {
            var bytes = Convert.FromBase64String(base64Image);
            using var client = new HttpClient();
            using var content = new MultipartFormDataContent();

            // Передаём ключ и секрет как form-data
            var apiKeyContent = new StringContent(_faceApiKey);
            apiKeyContent.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
            {
                Name = "\"api_key\""
            };
            content.Add(apiKeyContent);

            var apiSecretContent = new StringContent(_faceApiSecret);
            apiSecretContent.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
            {
                Name = "\"api_secret\""
            };
            content.Add(apiSecretContent);

            // Добавляем изображение
            var byteContent = new ByteArrayContent(bytes);
            byteContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Add(byteContent, "image_file", "gesture.jpg");

            var response = await client.PostAsync("https://api-us.faceplusplus.com/humanbodypp/v1/gesture", content);
            var json = await response.Content.ReadAsStringAsync();
            Console.WriteLine("🎯 Жест распознан: " + json);

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("hands", out var handsArray) && handsArray.GetArrayLength() > 0)
                {
                    var gestureObj = handsArray[0].GetProperty("gesture");

                    foreach (var gesture in gestureObj.EnumerateObject())
                    {
                        if (gesture.Value.TryGetDouble(out var confidence) && confidence > 0.8)
                        {
                            Console.WriteLine($"✅ Найден жест: {gesture.Name} (уверенность: {confidence})");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ Ошибка обработки JSON: " + ex.Message);
            }

            return false;
        }



        [AllowAnonymous]
        [HttpPost("gesture-check")]
        public async Task<IActionResult> CheckGesture([FromBody] FaceLoginRequest request)
        {
            try
            {
                Console.WriteLine("🤖 Проверка жеста...");
                bool isGestureDetected = await CheckForGesture(request.Base64Image);

                if (isGestureDetected)
                {
                    Console.WriteLine("✋ Жест обнаружен!");
                    return Ok(new { success = true });
                }

                Console.WriteLine("👎 Жест не найден.");
                return Ok(new { success = false });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"🔥 Ошибка при проверке жеста: {ex.Message}");
                return StatusCode(500, "Ошибка сервера при проверке жеста");
            }
        }

        private async Task<bool> CompareFaces(byte[] img1, byte[] img2)
        {
            using var client = new HttpClient();
            using var content = new MultipartFormDataContent();

            var apiKeyContent = new StringContent(_faceApiKey);
            apiKeyContent.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data") { Name = "\"api_key\"" };
            content.Add(apiKeyContent);

            var apiSecretContent = new StringContent(_faceApiSecret);
            apiSecretContent.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data") { Name = "\"api_secret\"" };
            content.Add(apiSecretContent);

            content.Add(new ByteArrayContent(img1)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
            }, "image_file1", "face1.jpg");
            content.Add(new ByteArrayContent(img2)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
            }, "image_file2", "face2.jpg");

            Console.WriteLine("📤 Отправляем запрос на Face++...");
            var response = await client.PostAsync("https://api-us.faceplusplus.com/facepp/v3/compare", content);
            var json = await response.Content.ReadAsStringAsync();
            Console.WriteLine("🧾 Ответ Face++: " + json);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine("❌ Ошибка от Face++: " + json);
                return false;
            }

            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("confidence", out var confidence))
            {
                var score = confidence.GetDouble();
                Console.WriteLine($"🎯 Сходство: {score}%");
                return score > 80; // Уменьшил порог с 85 до 65
            }

            Console.WriteLine("⚠️ Не удалось определить сходство.");
            return false;
        }


    }

}