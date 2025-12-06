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
        private readonly SariService _sari;

        private const string ItemCollection = "Items";
        private const string OrderCollection = "Orders";
        private const string StoreCollection = "Stores";

        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        public ReportsModel(IMongoDatabase db, SariService sari)
        {
            _db = db;
            _sari = sari;
        }

        // --- PROPERTIES ---
        public decimal TotalRevenue { get; set; }
        public decimal TotalProfit { get; set; }
        public int TotalOrders { get; set; }
        public decimal ForecastedRevenue { get; set; }
        public List<Item> LowStockItems { get; set; } = new();
        public List<Item> SlowMovingItems { get; set; } = new();
        public List<TopItemDto> TopSellingItems { get; set; } = new();
        public List<Order> RecentOrders { get; set; } = new();

        // --- CHARTS & UI ---
        public string CategoryChartLabels { get; set; } = "[]";
        public string CategoryChartData { get; set; } = "[]";
        public string SalesChartLabels { get; set; } = "[]";
        public string SalesChartData { get; set; } = "[]";
        public string ForecastChartData { get; set; } = "[]";
        public string HolidayPrediction { get; set; } = "Click 'Re-Analyze' to generate AI insights.";
        public List<string> ActionableTips { get; set; } = new();
        public DateTime? LastAnalysisDate { get; set; }

        public async Task OnGetAsync()
        {
            var storeId = await GetStoreIdAsync();
            if (string.IsNullOrEmpty(storeId)) return;

            // 1. Load Data
            var store = await _db.GetCollection<Store>(StoreCollection).Find(s => s.Id == storeId).FirstOrDefaultAsync();
            var items = await _db.GetCollection<Item>(ItemCollection).Find(i => i.StoreId == storeId).ToListAsync();
            var orders = await _db.GetCollection<Order>(OrderCollection)
                                  .Find(o => o.StoreId == storeId)
                                  .SortByDescending(o => o.OrderDate)
                                  .ToListAsync();

            // 2. Calculate KPIs
            RecentOrders = orders.Take(20).ToList();
            TotalRevenue = orders.Sum(o => o.TotalAmount);
            TotalOrders = orders.Count;
            TotalProfit = orders.SelectMany(o => o.Items).Sum(i => (i.Price - i.Cost) * i.Quantity);

            LowStockItems = items.Where(i => i.Quantity < 5).OrderBy(i => i.Quantity).ToList();

            // Slow Moving: Items with stock > 10 that haven't sold recently
            var soldItemNames = orders.SelectMany(o => o.Items).Select(i => i.ItemName).Distinct().ToHashSet();
            SlowMovingItems = items.Where(i => i.Quantity > 10 && !soldItemNames.Contains(i.Name)).Take(5).ToList();

            // 3. Prepare Charts
            PrepareSalesChart(orders);
            PrepareCategoryChart(items, orders);
            PrepareTopSellers(orders);

            // 4. Load Cached AI Data
            if (store != null && store.LastReport != null)
            {
                ForecastChartData = JsonSerializer.Serialize(store.LastReport.Forecast);
                ForecastedRevenue = store.LastReport.ForecastedRevenue;
                HolidayPrediction = store.LastReport.HolidayNote;
                ActionableTips = store.LastReport.Tips;
                LastAnalysisDate = store.LastReport.LastAnalysisDate;
            }
        }

        public async Task<IActionResult> OnPostAnalyzeAsync()
        {
            var storeId = await GetStoreIdAsync();
            if (string.IsNullOrEmpty(storeId)) return RedirectToPage();

            var orders = await _db.GetCollection<Order>(OrderCollection).Find(o => o.StoreId == storeId).ToListAsync();

            // --- FIX: HANDLE NO DATA CASE ---
            if (orders.Count < 5)
            {
                TempData["error"] = "Not enough data! Please click 'Gen Data' to create sample sales history first.";
                return RedirectToPage();
            }

            // Prepare Data for AI
            var last7Days = Enumerable.Range(0, 7).Select(i => DateTime.UtcNow.Date.AddDays(-6 + i)).ToList();
            var salesMap = new Dictionary<string, decimal>();
            foreach (var date in last7Days)
            {
                salesMap[date.ToString("yyyy-MM-dd")] = orders.Where(o => o.OrderDate.Date == date).Sum(o => o.TotalAmount);
            }

            var aiData = await GenerateAiForecast(salesMap);

            if (aiData != null)
            {
                var cache = new AiReportCache
                {
                    LastAnalysisDate = DateTime.UtcNow,
                    Forecast = aiData.Forecast,
                    ForecastedRevenue = aiData.Forecast.Sum(),
                    HolidayNote = aiData.HolidayNote,
                    Tips = aiData.Tips
                };

                var update = Builders<Store>.Update.Set(s => s.LastReport, cache);
                await _db.GetCollection<Store>(StoreCollection).UpdateOneAsync(s => s.Id == storeId, update);
                TempData["success"] = "AI Analysis Complete!";
            }
            else
            {
                TempData["error"] = "AI Service busy. Please try again in a moment.";
            }

            return RedirectToPage();
        }

        // --- DATA GENERATOR (SEEDS ORDERS) ---
        public async Task<IActionResult> OnPostSeedHistoryAsync()
        {
            var storeId = await GetStoreIdAsync();
            if (string.IsNullOrEmpty(storeId)) return RedirectToPage();

            var items = await _db.GetCollection<Item>(ItemCollection).Find(i => i.StoreId == storeId).ToListAsync();
            if (items.Count == 0) return RedirectToPage(); // Need items to sell

            var ordersCollection = _db.GetCollection<Order>(OrderCollection);
            var newOrders = new List<Order>();
            var random = new Random();

            // Create fake sales for the last 14 days
            for (int i = 14; i >= 0; i--)
            {
                if (random.NextDouble() > 0.8) continue; // 20% chance of no sales that day

                var date = DateTime.UtcNow.AddDays(-i);
                int dailyOrders = random.Next(1, 6); // 1-5 orders per day

                for (int j = 0; j < dailyOrders; j++)
                {
                    var orderItems = new List<CartItemDetail>();
                    int itemsInOrder = random.Next(1, 3);

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
                        OrderCode = $"SIM-{random.Next(1000, 9999)}",
                        Status = "Completed",
                        OrderDate = date,
                        Items = orderItems,
                        TotalAmount = orderItems.Sum(x => x.Total)
                    });
                }
            }

            if (newOrders.Count > 0) await ordersCollection.InsertManyAsync(newOrders);
            TempData["success"] = $"Generated {newOrders.Count} historical orders for analysis.";
            return RedirectToPage();
        }

        // --- HELPER METHODS ---

        private async Task<AiReportData?> GenerateAiForecast(Dictionary<string, decimal> pastSales)
        {
            var keys = _sari.GetDecryptedKeys();
            var baseUrl = _sari.GetFullApiUrl();

            // Strictly formatted prompt to prevent AI chatting back
            var context = JsonSerializer.Serialize(pastSales);
            string prompt = $"DATA: {context}. TASK: Predict sales for next 7 days. Identify 1 holiday trend. Give 3 short business tips. RESPONSE FORMAT (JSON ONLY): {{ \"forecast\": [100.00, 120.50, ...], \"holidayNote\": \"string\", \"tips\": [\"string\", \"string\", \"string\"] }}";

            using var client = new HttpClient();
            var content = new StringContent(JsonSerializer.Serialize(new { contents = new[] { new { parts = new[] { new { text = prompt } } } } }), Encoding.UTF8, "application/json");

            foreach (var key in keys)
            {
                try
                {
                    var res = await client.PostAsync($"{baseUrl}?key={key}", content);
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("candidates", out var candidates))
                        {
                            var text = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                            // Clean markdown
                            text = text.Replace("```json", "").Replace("```", "").Trim();
                            return JsonSerializer.Deserialize<AiReportData>(text, _jsonOptions);
                        }
                    }
                }
                catch { continue; }
            }
            return null;
        }

        private void PrepareSalesChart(List<Order> orders)
        {
            var last7Days = Enumerable.Range(0, 7).Select(i => DateTime.UtcNow.Date.AddDays(-6 + i)).ToList();
            var salesMap = new Dictionary<string, decimal>();
            foreach (var date in last7Days)
            {
                salesMap[date.ToString("MMM dd")] = orders.Where(o => o.OrderDate.Date == date).Sum(o => o.TotalAmount);
            }
            SalesChartLabels = JsonSerializer.Serialize(salesMap.Keys);
            SalesChartData = JsonSerializer.Serialize(salesMap.Values);
        }

        private void PrepareTopSellers(List<Order> orders)
        {
            var allItems = orders.SelectMany(o => o.Items);
            TopSellingItems = allItems.GroupBy(i => i.ItemName)
                .Select(g => new TopItemDto { Name = g.Key, QuantitySold = g.Sum(x => x.Quantity), TotalRevenue = g.Sum(x => x.Total) })
                .OrderByDescending(x => x.QuantitySold)
                .Take(5)
                .ToList();
        }

        private void PrepareCategoryChart(List<Item> items, List<Order> orders)
        {
            var catMap = items.ToDictionary(i => i.Name, i => i.Category);
            var catRevenue = new Dictionary<string, decimal>();
            foreach (var oItem in orders.SelectMany(o => o.Items))
            {
                string cat = catMap.ContainsKey(oItem.ItemName) ? catMap[oItem.ItemName] : "General";
                if (!catRevenue.ContainsKey(cat)) catRevenue[cat] = 0;
                catRevenue[cat] += oItem.Total;
            }
            CategoryChartLabels = JsonSerializer.Serialize(catRevenue.Keys);
            CategoryChartData = JsonSerializer.Serialize(catRevenue.Values);
        }

        private async Task<string?> GetStoreIdAsync()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return null;
            var store = await _db.GetCollection<Store>(StoreCollection).Find(s => s.OwnerId == userId).FirstOrDefaultAsync();
            return store?.Id;
        }

        public class AiReportData
        {
            public List<decimal> Forecast { get; set; } = new();
            public string HolidayNote { get; set; } = "No forecast data.";
            public List<string> Tips { get; set; } = new();
        }

        public class TopItemDto
        {
            public string Name { get; set; } = "";
            public int QuantitySold { get; set; }
            public decimal TotalRevenue { get; set; }
        }
    }
}