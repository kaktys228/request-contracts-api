using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Data.Common;
using static APIdIplom.Controllers.UserController;
using BCrypt.Net;

namespace APIdIplom.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UserController : ControllerBase
    {
        private readonly string _connectionString;

        public UserController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }
        public class UserDto
        {
            public int UserID { get; set; }
            public string Username { get; set; }
            public string FirstName { get; set; }
            public string MiddleName { get; set; }
            public string LastName { get; set; }
            public string Email { get; set; }
            public int RoleID { get; set; }
            public string RoleName { get; set; }    // добавили
            public int? GenderID { get; set; }
            public string GenderName { get; set; }
            public string MainImageBase64 { get; set; }
        }

        [HttpGet("users")]
        public async Task<IActionResult> GetUsers()
        {
            try
            {
                const string query = @"
            SELECT 
                u.userid,
                u.username,
                u.firstname,
                u.middlename,
                u.lastname,
                u.email,
                r.roleid,
                r.name,
                g.genderid,
                g.gendername
            FROM users u
            LEFT JOIN role r ON u.role_id = r.roleid
            LEFT JOIN gender g ON u.gender_id = g.genderid;";

                var users = new List<UserDto>();

                using (var connection = new NpgsqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = new NpgsqlCommand(query, connection))
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var user = new UserDto
                            {
                                UserID = reader.GetInt32(reader.GetOrdinal("userid")),
                                Username = reader.GetString(reader.GetOrdinal("username")),
                                FirstName = reader.GetString(reader.GetOrdinal("firstname")),
                                MiddleName = reader.IsDBNull(reader.GetOrdinal("middlename")) ? null : reader.GetString(reader.GetOrdinal("middlename")),
                                LastName = reader.GetString(reader.GetOrdinal("lastname")),
                                Email = reader.GetString(reader.GetOrdinal("email")),
                                RoleID = reader.GetInt32(reader.GetOrdinal("roleid")),
                                RoleName = reader.GetString(reader.GetOrdinal("name")),
                                GenderID = reader.IsDBNull(reader.GetOrdinal("genderid")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("genderid")),
                                GenderName = reader.IsDBNull(reader.GetOrdinal("gendername")) ? null : reader.GetString(reader.GetOrdinal("gendername")),
                                // Не загружаем картинку тут
                                MainImageBase64 = null
                            };

                            users.Add(user);
                        }
                    }
                }

                return Ok(users);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Ошибка сервера: {ex.Message}");
            }
        }


        public class UserDtos
        {
            public string Username { get; set; }
            public string Password { get; set; } // Новое поле для пароля
            public string FirstName { get; set; }
            public string MiddleName { get; set; }
            public string LastName { get; set; }
            public string Email { get; set; }
            public int RoleID { get; set; }
            public int? GenderID { get; set; }
            public string? MainImageBase64 { get; set; } // <- теперь это НЕ обязательное поле
        }


        [HttpPost("adduser")]
    public async Task<IActionResult> AddUser([FromBody] UserDtos newUser)
    {
        try
        {
            // Хешируем пароль
            string hashedPassword = BCrypt.Net.BCrypt.HashPassword(newUser.Password);

            const string query = @"
        INSERT INTO users (username, passwordhash, firstname, middlename, lastname, email, role_id, gender_id, mainimage)
        VALUES (@Username, @PasswordHash, @FirstName, @MiddleName, @LastName, @Email, @RoleID, @GenderID, @MainImageBase64)";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var command = new NpgsqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("Username", newUser.Username);
                    command.Parameters.AddWithValue("PasswordHash", hashedPassword); // Сохраняем хеш пароля
                    command.Parameters.AddWithValue("FirstName", newUser.FirstName);
                    command.Parameters.AddWithValue("MiddleName", (object)newUser.MiddleName ?? DBNull.Value);
                    command.Parameters.AddWithValue("LastName", newUser.LastName);
                    command.Parameters.AddWithValue("Email", newUser.Email);
                    command.Parameters.AddWithValue("RoleID", newUser.RoleID);
                    command.Parameters.AddWithValue("GenderID", (object)newUser.GenderID ?? DBNull.Value);

                    // Обработка изображения
                    if (!string.IsNullOrEmpty(newUser.MainImageBase64))
                    {
                        byte[] imageBytes = Convert.FromBase64String(newUser.MainImageBase64);
                        command.Parameters.AddWithValue("MainImageBase64", imageBytes);
                    }
                    else
                    {
                        command.Parameters.AddWithValue("MainImageBase64", DBNull.Value);
                    }

                    int rowsAffected = await command.ExecuteNonQueryAsync();

                    if (rowsAffected > 0)
                    {
                        return Ok("Пользователь успешно добавлен");
                    }
                    else
                    {
                        return StatusCode(400, "Не удалось добавить пользователя");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Ошибка сервера: {ex.Message}");
        }
    }

        [HttpDelete("deleteuser/{userId}")]
        public async Task<IActionResult> DeleteUser(int userId)
        {
            try
            {
                const string query = @"
            DELETE FROM users 
            WHERE userid = @UserID";

                using (var connection = new NpgsqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("UserID", userId);
                        int rowsAffected = await command.ExecuteNonQueryAsync();

                        if (rowsAffected > 0)
                        {
                            return Ok("Пользователь успешно удален");
                        }
                        else
                        {
                            return NotFound("Пользователь не найден");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Ошибка сервера: {ex.Message}");
            }
        }

        [HttpGet("userimage/{userId}")]
        public async Task<IActionResult> GetUserImage(int userId)
        {
            try
            {
                const string query = @"
            SELECT mainimage
            FROM users
            WHERE userid = @UserID";

                using (var connection = new NpgsqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("UserID", userId);

                        var result = await command.ExecuteScalarAsync();

                        if (result == null || result == DBNull.Value)
                        {
                            return NotFound("Изображение не найдено");
                        }

                        var imageBytes = (byte[])result;

                        // Преобразуем изображение в Base64
                        var base64Image = Convert.ToBase64String(imageBytes);

                        // Отправляем строку base64 как ответ
                        return Ok(base64Image);
                    }
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Ошибка сервера: {ex.Message}");
            }
        }

        public class UserDto1
        {
            public int UserID { get; set; }
            public string Username { get; set; }
            public string FirstName { get; set; }
            public string MiddleName { get; set; }
            public string LastName { get; set; }
            public string Email { get; set; }
            public int RoleID { get; set; }
            public string RoleName { get; set; }
            public int? GenderID { get; set; }
            public string GenderName { get; set; }
            public string MainImageBase64 { get; set; }
            public string Password { get; set; } // Новое поле для пароля
        }

        [HttpGet("getuser/{userId}")]
        public async Task<IActionResult> GetUserById(int userId)
        {
            try
            {
                const string query = @"
        SELECT 
            u.userid,
            u.username,
            u.firstname,
            u.middlename,
            u.lastname,
            u.email,
            r.roleid,
            r.name AS rolename,
            g.genderid,
            g.gendername,
            u.passwordhash -- Не возвращаем фото
        FROM users u
        LEFT JOIN role r ON u.role_id = r.roleid
        LEFT JOIN gender g ON u.gender_id = g.genderid
        WHERE u.userid = @UserId";

                using (var connection = new NpgsqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("UserId", userId);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                var user = new UserDto1
                                {
                                    UserID = reader.GetInt32(reader.GetOrdinal("userid")),
                                    Username = reader.GetString(reader.GetOrdinal("username")),
                                    FirstName = reader.GetString(reader.GetOrdinal("firstname")),
                                    MiddleName = reader.IsDBNull(reader.GetOrdinal("middlename")) ? null : reader.GetString(reader.GetOrdinal("middlename")),
                                    LastName = reader.GetString(reader.GetOrdinal("lastname")),
                                    Email = reader.GetString(reader.GetOrdinal("email")),
                                    RoleID = reader.IsDBNull(reader.GetOrdinal("roleid")) ? 0 : reader.GetInt32(reader.GetOrdinal("roleid")),
                                    RoleName = reader.IsDBNull(reader.GetOrdinal("rolename")) ? null : reader.GetString(reader.GetOrdinal("rolename")),
                                    GenderID = reader.IsDBNull(reader.GetOrdinal("genderid")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("genderid")),
                                    GenderName = reader.IsDBNull(reader.GetOrdinal("gendername")) ? null : reader.GetString(reader.GetOrdinal("gendername")),
                                    Password = reader.GetString(reader.GetOrdinal("passwordhash")) // Возвращаем хеш пароля
                                };

                                return Ok(user);
                            }
                            else
                            {
                                return NotFound($"Пользователь с ID {userId} не найден");
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

        // В контроллере API
        [HttpPut("updateuser/{userId}")]
        public async Task<IActionResult> UpdateUser(int userId, [FromBody] UserUpdateDto editUser)
        {
            try
            {
                const string query = @"
UPDATE users
SET 
    username = @Username,
    firstname = @FirstName,
    middlename = @MiddleName,
    lastname = @LastName,
    email = @Email,
    role_id = @RoleID,
    gender_id = @GenderID,
    mainimage = @MainImageBase64
WHERE userid = @UserID";

                using (var connection = new NpgsqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("UserID", userId);
                        command.Parameters.AddWithValue("Username", editUser.Username);
                        command.Parameters.AddWithValue("FirstName", editUser.FirstName);
                        command.Parameters.AddWithValue("MiddleName", (object)editUser.MiddleName ?? DBNull.Value);
                        command.Parameters.AddWithValue("LastName", editUser.LastName);
                        command.Parameters.AddWithValue("Email", editUser.Email);
                        command.Parameters.AddWithValue("RoleID", editUser.RoleID);
                        command.Parameters.AddWithValue("GenderID", (object)editUser.GenderID ?? DBNull.Value);

                        // Обработка изображения
                        if (!string.IsNullOrEmpty(editUser.MainImageBase64))
                        {
                            byte[] imageBytes = Convert.FromBase64String(editUser.MainImageBase64);
                            command.Parameters.AddWithValue("MainImageBase64", imageBytes);
                        }
                        else
                        {
                            command.Parameters.AddWithValue("MainImageBase64", DBNull.Value);
                        }

                        int rowsAffected = await command.ExecuteNonQueryAsync();

                        if (rowsAffected > 0)
                        {
                            return Ok("Данные пользователя успешно обновлены");
                        }
                        else
                        {
                            return NotFound("Пользователь не найден");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Ошибка сервера: {ex.Message}");
            }
        }

        [HttpPut("updatepassword/{userId}")]
        public async Task<IActionResult> UpdatePassword(int userId, [FromBody] UserPasswordDto passwordDto)
        {
            try
            {
                if (string.IsNullOrEmpty(passwordDto.NewPassword))
                {
                    return BadRequest("Новый пароль не может быть пустым");
                }

                string hashedPassword = BCrypt.Net.BCrypt.HashPassword(passwordDto.NewPassword);

                const string query = @"
UPDATE users
SET passwordhash = @PasswordHash
WHERE userid = @UserID";

                using (var connection = new NpgsqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("UserID", userId);
                        command.Parameters.AddWithValue("PasswordHash", hashedPassword);

                        int rowsAffected = await command.ExecuteNonQueryAsync();

                        if (rowsAffected > 0)
                        {
                            return Ok("Пароль успешно изменен");
                        }
                        else
                        {
                            return NotFound("Пользователь не найден");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Ошибка сервера: {ex.Message}");
            }
        }

        public class UserUpdateDto
        {
            public string Username { get; set; }
            public string FirstName { get; set; }
            public string MiddleName { get; set; }
            public string LastName { get; set; }
            public string Email { get; set; }
            public int RoleID { get; set; }
            public int? GenderID { get; set; }
            public string MainImageBase64 { get; set; }
        }

        public class UserPasswordDto
        {
            public string NewPassword { get; set; }
        }


        [HttpPost("checkexisting")]
        public async Task<IActionResult> CheckExisting([FromBody] CheckExistingRequest request)
        {
            try
            {
                const string checkUsernameQuery = "SELECT 1 FROM users WHERE username = @Username";
                const string checkEmailQuery = "SELECT 1 FROM users WHERE email = @Email";

                using (var connection = new NpgsqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    // 1. Проверка username
                    using (var command = new NpgsqlCommand(checkUsernameQuery, connection))
                    {
                        command.Parameters.AddWithValue("Username", request.Username);
                        var usernameExists = await command.ExecuteScalarAsync() != null;

                        if (usernameExists)
                            return BadRequest("Username already exists");
                    }

                    // 2. Проверка email
                    using (var command = new NpgsqlCommand(checkEmailQuery, connection))
                    {
                        command.Parameters.AddWithValue("Email", request.Email);
                        var emailExists = await command.ExecuteScalarAsync() != null;

                        if (emailExists)
                            return BadRequest("Email already exists");
                    }

                    // Здесь мы убираем проверку пароля на существующие хеши,
                    // так как это не нужно при добавлении нового пользователя
                }

                return Ok();
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error checking existing data: {ex.Message}");
            }
        }

        public class CheckExistingRequest
        {
            public string Username { get; set; }
            public string Email { get; set; }
        }






    }
}
