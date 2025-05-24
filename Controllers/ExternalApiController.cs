using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json.Serialization;

namespace APIdIplom.Controllers
{
    [ApiController]
    [Route("external-api")]
    public class ExternalApiController : ControllerBase
    {
        [HttpGet("validate-inn/{inn}")]
        public async Task<IActionResult> ValidateInn(string inn)
        {
            try
            {
                using var client = new HttpClient();

                client.DefaultRequestHeaders.Remove("Authorization");
                client.DefaultRequestHeaders.Add("Authorization", "Token 91bca5744375b62873ca02ba6acb51e66f6261b9");

                var jsonBody = System.Text.Json.JsonSerializer.Serialize(new { query = inn });
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                var response = await client.PostAsync("https://suggestions.dadata.ru/suggestions/api/4_1/rs/findById/party", content);
                var raw = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return StatusCode((int)response.StatusCode, $"Ошибка от DaData: {raw}");

                var json = System.Text.Json.JsonSerializer.Deserialize<DaDataResponse>(raw, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                var party = json?.Suggestions?.FirstOrDefault()?.Data;
                if (party == null)
                    return NotFound("ИНН не найден в реальности");

                return Ok(new
                {
                    Warning = $"⚠️ Внимание: организация имеет статус «{party.State.Status}».",
                    Status = party.State.Status,
                    ActualityDate = DateTimeOffset.FromUnixTimeMilliseconds(party.State.ActualityDate).DateTime.ToString("dd.MM.yyyy"),
                    INN = party.Inn,
                    KPP = party.Kpp,
                    OGRN = party.Ogrn,

                    Name = party.Name?.FullWithOpf ?? "(нет полного наименования)",
                    ShortName = party.Name?.ShortWithOpf ?? "(нет сокращённого наименования)",

                    PostalAddress = party.Address?.Data?.Source ?? party.Address?.Value ?? "",
                    Address = party.Address?.Value ?? "",

                    KPP_KN = "",  // ввод вручную
                    Phone = "",   // нет в API
                    Email = ""    // нет в API
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, "Ошибка при проверке ИНН: " + ex.Message);
            }
        }


        // DTO модели для парсинга ответа
        public class DaDataResponse
        {
            public List<Suggestion> Suggestions { get; set; }
        }

        public class Suggestion
        {
            public PartyData Data { get; set; }
        }

        public class PartyData
        {
            [JsonPropertyName("inn")]
            public string Inn { get; set; }

            [JsonPropertyName("kpp")]
            public string Kpp { get; set; }

            [JsonPropertyName("ogrn")]
            public string Ogrn { get; set; }

            [JsonPropertyName("name")]
            public NameObject Name { get; set; }

            [JsonPropertyName("address")]
            public AddressObject Address { get; set; }
            [JsonPropertyName("state")]
            public StateInfo State { get; set; }

            // ⚠️ Не добавляй поле KPP_KN, если его нет в DaData, иначе будет конфликт
        }
        public class StateInfo
        {
            [JsonPropertyName("status")]
            public string Status { get; set; }  // ACTIVE, BANKRUPT, LIQUIDATING и т.п.

            [JsonPropertyName("code")]
            public string Code { get; set; }

            [JsonPropertyName("actuality_date")]
            public long ActualityDate { get; set; } // timestamp
        }

        public class NameObject
        {
            [System.Text.Json.Serialization.JsonPropertyName("full_with_opf")]
            public string FullWithOpf { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("short_with_opf")]
            public string ShortWithOpf { get; set; }
        }
        public class AddressData
        {
            [JsonPropertyName("source")]
            public string Source { get; set; }
        }

        public class AddressObject
        {
            [JsonPropertyName("value")]
            public string Value { get; set; }

            [JsonPropertyName("data")]
            public AddressData Data { get; set; }
        }
    }
}
