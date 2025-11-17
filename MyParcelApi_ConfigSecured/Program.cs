using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
//using Microsoft.OpenApi.Models;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

string TrimToMax(string? input, int max) =>
    string.IsNullOrEmpty(input) ? "" : input.Length > max ? input.Substring(0, max) : input;

/* (string Street, string PostalCode, string City, string CountryCode) ParseAddress(string rawAddress)
{
    string[] lines = rawAddress.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    string street = lines.Length > 0 ? lines[0].Trim() : "";
    string cityLine = lines.Length > 1 ? lines[1].Trim() : "";
    string countryRaw = lines.Length > 2 ? lines[2].Trim().ToUpper() : "";

    string postalCode = "";
    string city = cityLine;

    var match = Regex.Match(cityLine, @"\b(\d{4})\s?([A-Z]{2})\b");
    if (match.Success)
    {
        postalCode = match.Groups[1].Value + match.Groups[2].Value;
        city = cityLine.Substring(match.Index + match.Length).Trim();
    }

    string countryCode = countryRaw switch
    {
        "NETHERLANDS" => "NL",
        "GERMANY" => "DE",
        "BELGIUM" => "BE",
        _ => "NL"
    };

    return (street, postalCode, city, countryCode);
} */

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddEndpointsApiExplorer();

var config = builder.Configuration;
var app = builder.Build();

app.MapPost("/api/shipment", async (HttpContext context, DeliveryRequest input, IHttpClientFactory httpClientFactory) =>
{
    try
    {
        var sapBaseUrl = config["SAP:BaseUrl"];
        var sapCompany = config["SAP:CompanyDB"];
        var sapUsername = config["SAP:Username"];
        var sapPassword = config["SAP:Password"];
        var myParcelToken = config["MyParcel:ApiKey"]; 

        var client = httpClientFactory.CreateClient();

        var loginResponse = await client.PostAsync($"{sapBaseUrl}Login", new StringContent(JsonSerializer.Serialize(new
        {
            CompanyDB = sapCompany,
            UserName = sapUsername,
            Password = sapPassword
        }), Encoding.UTF8, "application/json"));

        if (!loginResponse.IsSuccessStatusCode)
        {
            var error = await loginResponse.Content.ReadAsStringAsync();
            app.Logger.LogError("SAP Login failed: {0}", error);
            return Results.Problem($"SAP Login failed: {error}");
        }

        var cookies = loginResponse.Headers.GetValues("Set-Cookie").ToArray();
        var sessionCookie = cookies.FirstOrDefault(c => c.Contains("B1SESSION"))?.Split(';')[0];
        var routeCookie = cookies.FirstOrDefault(c => c.Contains("ROUTEID"))?.Split(';')[0];

        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("Cookie", $"{sessionCookie}; {routeCookie}");

        var deliveryRes = await client.GetAsync($"{sapBaseUrl}DeliveryNotes({input.DocEntry})");

        if (!deliveryRes.IsSuccessStatusCode)
            return Results.Problem("Failed to fetch delivery");

        var deliveryJson = await deliveryRes.Content.ReadAsStringAsync();
        app.Logger.LogInformation("Full SAP response: {0}", deliveryJson);
        using var jsonDoc = JsonDocument.Parse(deliveryJson);
        var root = jsonDoc.RootElement;
        //  Get CardCode from delivery note
        var cardCode = root.TryGetProperty("CardCode", out var cardCodeVal) ? cardCodeVal.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(cardCode))
            return Results.Problem("CardCode not found in delivery note");

        app.Logger.LogInformation("Calling BP API with CardCode: {0}", cardCode);
        // Fetch Business Partner details
        var bpResponse = await client.GetAsync($"{sapBaseUrl}BusinessPartners('{cardCode}')");
        if (!bpResponse.IsSuccessStatusCode)
            return Results.Problem($"Failed to fetch Business Partner for CardCode: {cardCode}");

        var bpJson = await bpResponse.Content.ReadAsStringAsync();
        using var bpDoc = JsonDocument.Parse(bpJson);
        var bpRoot = bpDoc.RootElement;
        var bpValue = bpDoc.RootElement;

/*         // 1️⃣ Ensure "value" array exists and has at least 1 item
                if (!bpRoot.TryGetProperty("value", out var valueArray) || valueArray.GetArrayLength() == 0)
                {
                    app.Logger.LogError("Business Partner API returned no results.");
                    return Results.Problem("No Business Partner data found for the given CardCode.");
                }

                // 2️⃣ Safe access of first item
                var bpValue = valueArray[0]; */

        string street = bpValue.TryGetProperty("Address", out var streetVal) ? streetVal.GetString() ?? "" : "";
        string postalCode = bpValue.TryGetProperty("ZipCode", out var zipVal) ? zipVal.GetString() ?? "0000XX" : "0000XX";
        string city = bpValue.TryGetProperty("City", out var cityval) ? cityval.GetString() ?? "" : ""; // You may extend logic if city is available separately
        string countryCode = bpValue.TryGetProperty("Country", out var countryval) ? countryval.GetString() ?? "" : "";  // Hardcoded unless available in BP

        string contactName = bpValue.TryGetProperty("ContactPerson", out var personVal) ? personVal.GetString() ?? "SAP Contact" : "SAP Contact";
        string phone = bpValue.TryGetProperty("Phone1", out var phoneVal) ? phoneVal.GetString() ?? "" : "";
        string email = bpValue.TryGetProperty("EmailAddress",out var EmailAddressval) ? EmailAddressval.GetString()??"" :"" ; // Use actual email field if available


        var recipient = new
        {
            cc = countryCode,
            region = "Zuid-Holland",
            city = TrimToMax(city, 40),
            street = TrimToMax(street, 40),
            //number = "1", // still hardcoded unless available
            postal_code = postalCode,
            person = contactName,
            phone = phone,
            email = email
        };
        var shipmentPayload = new
        {
            data = new
            {
                shipments = new[]
                {
                    new
                    {
                        reference_identifier = $"DEL-{input.DocEntry}",
                        recipient,
                        options = new
                        {
                            package_type = 1,
                            only_recipient = 1,
                            signature = 1,
                            @return = 1,
                            insurance = new { amount = 1, currency = "EUR" },
                            large_format = 0,
                            label_description = "Sent from SAP B1 Cloud",
                            age_check = 0
                        },
                        carrier = 1
                    }
                }
            }
        };

        var shipmentJson = JsonSerializer.Serialize(shipmentPayload);
        var myParcelClient = httpClientFactory.CreateClient();
        var myParcelContent = new StringContent(shipmentJson, Encoding.UTF8);
        myParcelContent.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.shipment+json")
        {
            CharSet = "utf-8",
            Parameters = { new NameValueHeaderValue("version", "1.1") }
        };
        myParcelClient.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(myParcelToken);
        myParcelClient.DefaultRequestHeaders.UserAgent.ParseAdd("CustomApiCall/2");

        var myParcelRes = await myParcelClient.PostAsync("https://api.myparcel.nl/shipments", myParcelContent);
        var myParcelResponse = await myParcelRes.Content.ReadAsStringAsync();

        return Results.Ok(new
        {
            Status = myParcelRes.IsSuccessStatusCode ? "Success" : "Error",
            SAP_DocEntry = input.DocEntry,
            MyParcel = myParcelResponse
        });
    }
    catch (Exception ex)
    {
        app.Logger.LogError("Unhandled exception: {0}", ex.ToString());
        return Results.Problem("Unhandled server error: " + ex.Message);
    }
});

app.Run();

record DeliveryRequest(int DocEntry);