using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Npgsql;
using static APIdIplom.Controllers.AutoresationController;
using System.Net.Mail;

namespace APIdIplom.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PasswordResetController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;
        private readonly JwtSettings _jwtSettings;
        private readonly SmtpClient _smtpClient;
        private readonly Dictionary<string, (string Code, DateTime Expiry)> _resetCodes = new();

        public PasswordResetController(
            IConfiguration configuration,
            IOptions<JwtSettings> jwtSettings,
            SmtpClient smtpClient)
        {
            _configuration = configuration;
            _connectionString = configuration.GetConnectionString("DefaultConnection");
            _jwtSettings = jwtSettings.Value;
            _smtpClient = smtpClient;
        }
        public class ResetPasswordRequest
        {
            public string Username { get; set; }
        }

        public class VerifyCodeRequest
        {
            public string Username { get; set; }
            public string Code { get; set; }
            public string NewPassword { get; set; }
        }

        [HttpPost("requestReset")]
        public async Task<IActionResult> RequestReset([FromBody] ResetPasswordRequest request)
        {
            try
            {
                // 1. Получаем email пользователя
                string userEmail = await GetUserEmail(request.Username);

                if (string.IsNullOrEmpty(userEmail))
                {
                    return BadRequest("Пользователь с таким логином не найден");
                }

                // 2. Генерируем код
                var code = GenerateRandomCode();
                var expiryTime = DateTime.UtcNow.AddMinutes(15);

                // 3. Сохраняем код в базу данных
                const string updateQuery = @"
            UPDATE users 
            SET reset_password_code = @Code,
                reset_password_code_expiry = @Expiry
            WHERE username = @Username";

                using (var connection = new NpgsqlConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    using (var command = new NpgsqlCommand(updateQuery, connection))
                    {
                        command.Parameters.AddWithValue("Code", code);
                        command.Parameters.AddWithValue("Expiry", expiryTime);
                        command.Parameters.AddWithValue("Username", request.Username);

                        await command.ExecuteNonQueryAsync();
                    }
                }
                var localTime = expiryTime.ToLocalTime();

                // 4. Отправляем письмо
                var emailBody = $@"
            <h2>Код восстановления пароля</h2>
            <p>Ваш код для восстановления пароля: <strong>{code}</strong></p>
            <p>Код действителен до {localTime:dd.MM.yyyy HH:mm} (местное время).</p>";
                var message = new MailMessage
                {
                    From = new MailAddress(
                        _configuration["Email:FromEmail"],
                        _configuration["Email:FromName"]),
                    To = { userEmail },
                    Subject = "Код восстановления пароля",
                    Body = emailBody,
                    IsBodyHtml = true
                };

                await _smtpClient.SendMailAsync(message);

                return Ok("Код восстановления отправлен на вашу почту.");
            }
            catch (Exception ex)
            {
                return StatusCode(500, "Ошибка при отправке кода.");
            }
        }

        [HttpPost("verifyCode")]
        public async Task<IActionResult> VerifyCode([FromBody] VerifyCodeRequest request)
        {
            try
            {
                // Валидация запроса
                if (string.IsNullOrEmpty(request.Username) ||
                    string.IsNullOrEmpty(request.Code) ||
                    string.IsNullOrEmpty(request.NewPassword))
                {
                    return BadRequest("Все поля обязательны для заполнения");
                }

                // 1. Проверяем код из базы данных
                const string checkQuery = @"
            SELECT reset_password_code, reset_password_code_expiry 
            FROM users 
            WHERE username = @Username";

                using (var connection = new NpgsqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    string dbCode = null;
                    DateTime? dbExpiry = null;

                    // Получаем код и время из базы
                    using (var command = new NpgsqlCommand(checkQuery, connection))
                    {
                        command.Parameters.AddWithValue("Username", request.Username);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                dbCode = reader.IsDBNull(0) ? null : reader.GetString(0);
                                dbExpiry = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1);
                            }
                        }
                    }

                    // Проверяем код
                    if (string.IsNullOrEmpty(dbCode) ||
                        dbExpiry == null ||
                        dbExpiry < DateTime.UtcNow ||
                        dbCode != request.Code)
                    {
                        return BadRequest("Неверный или просроченный код.");
                    }

                    // 2. Обновляем пароль и очищаем код
                    const string updateQuery = @"
                UPDATE users 
                SET passwordhash = @PasswordHash,
                    passwordchangeddate = @ChangeDate,
                    reset_password_code = NULL,
                    reset_password_code_expiry = NULL
                WHERE username = @Username";

                    using (var updateCommand = new NpgsqlCommand(updateQuery, connection))
                    {
                        updateCommand.Parameters.AddWithValue("Username", request.Username);
                        updateCommand.Parameters.AddWithValue("PasswordHash",
                            BCrypt.Net.BCrypt.HashPassword(request.NewPassword));
                        updateCommand.Parameters.AddWithValue("ChangeDate", DateTime.UtcNow);

                        await updateCommand.ExecuteNonQueryAsync();
                    }
                }

                return Ok("Пароль успешно изменен.");
            }
            catch (Exception ex)
            {
                return StatusCode(500, "Ошибка при обработке запроса.");
            }
        }

        private async Task<string> GetUserEmail(string username)
        {
            const string query = "SELECT email FROM users WHERE username = @Username";
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("Username", username);

            return (await command.ExecuteScalarAsync())?.ToString();
        }

      

        private string GenerateRandomCode()
        {
            var length = _configuration.GetValue<int>("PasswordReset:CodeLength");
            var random = new Random();
            return new string(Enumerable.Repeat("0123456789", length)
                .Select(s => s[random.Next(s.Length)])
                .ToArray());
        }
    }
}
