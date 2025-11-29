using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MongoDB.Driver;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration; // For AI
using System.Net.Http;
using System.Text;

namespace WebApplication11.Pages
{
    [Authorize]
    public class ReportsModel : PageModel
    {
        private readonly IMongoDatabase _db;
        private readonly IConfiguration _config;
        private const string ItemCollection = "Items";
        private const string OrderCollection = "Orders";

        public ReportsModel(IMongoDatabase db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        // --- KPI Data ---
        public decimal TotalRevenue { get; set; }
        public int TotalOrders { get; set; }
        public decimal ForecastedRevenue { get; set; } // AI Predicted
        public List<Item> LowStockItems { get; set; } = new();
        public List<Item> SlowMovingItems { get; set; } = new(); // High stock, no sales

        // --- Chart Data (JSON) ---
        public string SalesChartLabels { get; set; } = "[]";
        public string SalesChartData { get; set; } = "[]"; // Actual
        public string ForecastChartData { get; set; } = "[]"; // AI Predicted

        // --- AI Insights ---
        public string HolidayPrediction { get; set; } = "Ask Sari to analyze your upcoming holidays!";
        public List<string> ActionableTips { get; set; } = new();

        public async Task OnGetAsync()
        {
            var storeId = getStoreId(); // Helper to get current user's store
            if (string.IsNullOrEmpty(storeId)) return;

            // 1. Fetch Data
            var items = await _db.GetCollection<Item>(ItemCollection).Find(i => i.StoreId == storeId).ToListAsync();
            var orders = await _db.GetCollection<Order>(OrderCollection).Find(o => o.StoreId == storeId).ToListAsync();

            // 2. Calculate Basics
            TotalRevenue = orders.Sum(o => o.TotalAmount);
            TotalOrders = orders.Count;
            LowStockItems = items.Where(i => i.Quantity < 5).OrderBy(i => i.Quantity).ToList();

            // 3. Identify Slow Movers (Items with > 10 stock but 0 sales in orders)
            // Flatten all ordered items to a HashSet for fast lookup
            var soldItemNames = orders.SelectMany(o => o.Items).Select(i => i.ItemName).ToHashSet();
            SlowMovingItems = items.Where(i => i.Quantity > 10 && !soldItemNames.Contains(i.Name)).Take(5).ToList();

            // 4. Prepare Chart Data (Last 7 Days Sales)
            var last7Days = Enumerable.Range(0, 7).Select(i => DateTime.UtcNow.Date.AddDays(-6 + i)).ToList();
            var salesMap = new Dictionary<string, decimal>();

            foreach (var date in last7Days)
            {
                var label = date.ToString("MMM dd");
                var dailyTotal = orders.Where(o => o.OrderDate.Date == date).Sum(o => o.TotalAmount);
                salesMap[label] = dailyTotal;
            }

            SalesChartLabels = JsonSerializer.Serialize(salesMap.Keys);
            SalesChartData = JsonSerializer.Serialize(salesMap.Values);

            // 5. Call AI for Forecast (Async but blocking for page load - simple implementation)
            await GenerateAiForecast(items, orders, salesMap);
        }

        private async Task GenerateAiForecast(List<Item> items, List<Order> orders, Dictionary<string, decimal> pastSales)
        {
            string apiKey = _config["Gemini:ApiKey"];
            if (string.IsNullOrEmpty(apiKey)) return;

            // Prepare Context for AI
            var contextData = new
            {
                Date = DateTime.UtcNow.ToString("MMMM dd, yyyy"),
                InventorySample = items.Take(10).Select(i => new { i.Name, i.Category, i.Quantity }),
                PastSales = pastSales,
                TotalRevenue
            };

            string prompt = $@"
                You are Sari, a smart business analyst for a Sari-Sari Store in the Philippines.
                Here is my current store data: {JsonSerializer.Serialize(contextData)}
                
                Task 1: Predict sales for the NEXT 7 DAYS based on my past sales. Be realistic. Return an array of 7 decimal numbers.
                Task 2: Identify any upcoming Filipino holidays/events relevant to the current date and my inventory.
                Task 3: Give 3 short, specific tips to increase sales.

                Return ONLY raw JSON: 
                {{ 
                    ""forecast"": [100.00, 120.50, ...], 
                    ""holidayNote"": ""string"",
                    ""tips"": [""string"", ""string"", ""string""]
                }}";

            try
            {
                using var client = new HttpClient();
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key={apiKey}";
                var content = new StringContent(JsonSerializer.Serialize(new { contents = new[] { new { parts = new[] { new { text = prompt } } } } }), Encoding.UTF8, "application/json");

                var res = await client.PostAsync(url, content);
                var jsonStr = await res.Content.ReadAsStringAsync();

                // Parse AI Response
                using var doc = JsonDocument.Parse(jsonStr);
                var text = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();

                // Clean markdown
                text = text.Replace("```json", "").Replace("```", "").Trim();
                var aiData = JsonSerializer.Deserialize<AiReportData>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (aiData != null)
                {
                    ForecastChartData = JsonSerializer.Serialize(aiData.Forecast);
                    ForecastedRevenue = aiData.Forecast.Sum();
                    HolidayPrediction = aiData.HolidayNote;
                    ActionableTips = aiData.Tips;
                }
            }
            catch
            {
                // Fallback if AI fails
                ForecastChartData = SalesChartData; // Flat line
                HolidayPrediction = "Sari is having trouble connecting to the forecast server.";
            }
        }

        private string getStoreId() => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "";

        // Helper Class for AI JSON
        public class AiReportData
        {
            public List<decimal> Forecast { get; set; }
            public string HolidayNote { get; set; }
            public List<string> Tips { get; set; }
        }
    }
}