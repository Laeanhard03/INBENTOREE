using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MongoDB.Driver;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Text;
using System.Security.Claims;
using WebApplication11.Services;

namespace WebApplication11.Pages
{
    [Authorize]
    public class ReportsModel : PageModel
    {
        private readonly IMongoDatabase _db;
        private readonly IConfiguration _config;
        private const string ItemCollection = "Items";
        private const string OrderCollection = "Orders";
        private const string StoreCollection = "Stores";

        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        public ReportsModel(IMongoDatabase db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        // --- KPI Data ---
        public decimal TotalRevenue { get; set; }
        public decimal TotalProfit { get; set; }
        public int TotalOrders { get; set; }
        public decimal ForecastedRevenue { get; set; }
        public List<Item> LowStockItems { get; set; } = new();
        public List<Item> SlowMovingItems { get; set; } = new();

        // --- Transaction History ---
        public List<Order> RecentOrders { get; set; } = new();

        public string SalesChartLabels { get; set; } = "[]";
        public string SalesChartData { get; set; } = "[]";
        public string ForecastChartData { get; set; } = "[]";

        public string HolidayPrediction { get; set; } = "Ask Sari to analyze your upcoming holidays!";
        public List<string> ActionableTips { get; set; } = new();

        public async Task OnGetAsync()
        {
            var storeId = await GetStoreIdAsync();
            if (string.IsNullOrEmpty(storeId)) return;

            var items = await _db.GetCollection<Item>(ItemCollection).Find(i => i.StoreId == storeId).ToListAsync();

            var orders = await _db.GetCollection<Order>(OrderCollection)
                                  .Find(o => o.StoreId == storeId)
                                  .SortByDescending(o => o.OrderDate)
                                  .ToListAsync();

            RecentOrders = orders.Take(20).ToList();

            TotalRevenue = orders.Sum(o => o.TotalAmount);
            TotalOrders = orders.Count;
            TotalProfit = orders.SelectMany(o => o.Items).Sum(i => (i.Price - i.Cost) * i.Quantity);

            LowStockItems = items.Where(i => i.Quantity < 5).OrderBy(i => i.Quantity).ToList();
            var soldItemNames = orders.SelectMany(o => o.Items).Select(i => i.ItemName).ToHashSet();
            SlowMovingItems = items.Where(i => i.Quantity > 10 && !soldItemNames.Contains(i.Name)).Take(5).ToList();

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

            await GenerateAiForecast(items, orders, salesMap);
        }

        public async Task<IActionResult> OnPostSeedHistoryAsync()
        {
            var storeId = await GetStoreIdAsync();
            if (string.IsNullOrEmpty(storeId)) return RedirectToPage();

            var items = await _db.GetCollection<Item>(ItemCollection).Find(i => i.StoreId == storeId).ToListAsync();

            if (items.Count == 0)
            {
                var sampleItem = new Item { StoreId = storeId, Name = "Starter Pack", Price = 100, CostPrice = 80, Quantity = 100 };
                await _db.GetCollection<Item>(ItemCollection).InsertOneAsync(sampleItem);
                items.Add(sampleItem);
            }

            var ordersCollection = _db.GetCollection<Order>(OrderCollection);
            var newOrders = new List<Order>();
            var random = new Random();

            for (int i = 30; i >= 0; i--)
            {
                if (random.NextDouble() > 0.7) continue;

                var date = DateTime.UtcNow.AddDays(-i);
                int dailyOrders = random.Next(1, 8);

                for (int j = 0; j < dailyOrders; j++)
                {
                    var orderItems = new List<CartItemDetail>();
                    int itemsInOrder = random.Next(1, 4);

                    for (int k = 0; k < itemsInOrder; k++)
                    {
                        var item = items[random.Next(items.Count)];
                        orderItems.Add(new CartItemDetail
                        {
                            ItemName = item.Name,
                            Price = item.Price,
                            Cost = item.CostPrice,
                            Quantity = random.Next(1, 3)
                        });
                    }

                    newOrders.Add(new Order
                    {
                        StoreId = storeId,
                        CustomerName = "Walk-in Customer",
                        OrderCode = $"RND-{random.Next(10000, 99999)}",
                        Status = "Completed",
                        OrderDate = date,
                        Items = orderItems,
                        TotalAmount = orderItems.Sum(x => x.Total)
                    });
                }
            }

            if (newOrders.Count > 0) await ordersCollection.InsertManyAsync(newOrders);
            return RedirectToPage();
        }

        private async Task<string?> GetStoreIdAsync()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return null;
            var store = await _db.GetCollection<Store>(StoreCollection).Find(s => s.OwnerId == userId).FirstOrDefaultAsync();
            return store?.Id;
        }

        private async Task GenerateAiForecast(List<Item> items, List<Order> orders, Dictionary<string, decimal> pastSales)
        {
            string apiKey = EncryptionHelper.Decrypt(_config["Gemini:ApiKey"] ?? string.Empty);

            if (string.IsNullOrEmpty(apiKey))
            {
                HolidayPrediction = "API Key is missing in appsettings.json";
                return;
            }

            var contextData = new
            {
                Date = DateTime.UtcNow.ToString("MMMM dd, yyyy"),
                InventorySample = items.Take(10).Select(i => new { i.Name, i.Category, i.Quantity }),
                PastSales = pastSales,
                TotalRevenue,
                TotalProfit
            };

            string prompt = $"You are Sari, a smart business analyst. Analyze this data: {JsonSerializer.Serialize(contextData)}. " +
                            "1. Predict next 7 days sales (decimal array). 2. Identify holidays. 3. Give tips. " +
                            "Return JSON: { \"forecast\": [1.0, 2.0], \"holidayNote\": \"text\", \"tips\": [\"tip1\"] }";

            try
            {
                using var client = new HttpClient();
                // CHANGED: Switched to gemini-1.5-flash for better stability
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={apiKey}";

                var content = new StringContent(JsonSerializer.Serialize(new { contents = new[] { new { parts = new[] { new { text = prompt } } } } }), Encoding.UTF8, "application/json");
                var res = await client.PostAsync(url, content);

                // NEW: Explicit error checking
                if (!res.IsSuccessStatusCode)
                {
                    var errorMsg = await res.Content.ReadAsStringAsync();
                    throw new Exception($"API Error ({res.StatusCode})");
                }

                var jsonStr = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(jsonStr);

                if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                {
                    var text = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                    text = text.Replace("```json", "").Replace("```", "").Trim();
                    var aiData = JsonSerializer.Deserialize<AiReportData>(text, _jsonOptions);

                    if (aiData != null)
                    {
                        ForecastChartData = JsonSerializer.Serialize(aiData.Forecast);
                        ForecastedRevenue = aiData.Forecast.Sum();
                        HolidayPrediction = aiData.HolidayNote;
                        ActionableTips = aiData.Tips;
                    }
                }
                else
                {
                    throw new Exception("AI returned no content.");
                }
            }
            catch (Exception ex)
            {
                ForecastChartData = SalesChartData;
                // This will now show the REAL error on your dashboard
                HolidayPrediction = $"AI Error: {ex.Message}";
                ActionableTips.Add("Check API Key in configuration.");
            }
        }

        public class AiReportData
        {
            public List<decimal> Forecast { get; set; } = new();
            public string HolidayNote { get; set; } = string.Empty;
            public List<string> Tips { get; set; } = new();
        }
    }
}