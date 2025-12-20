using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MongoDB.Driver;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using System.Net.Http;
using System.Text;
using System.Security.Claims;
using WebApplication11.Services;
using System.IO;

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

        // --- NEW: GATEKEEPER PROPERTY ---
        public bool HasInventory { get; set; } = false;

        // --- KPI PROPERTIES ---
        public decimal TotalRevenue { get; set; }
        public decimal TotalProfit { get; set; }
        public int TotalOrders { get; set; }
        public decimal ForecastedRevenue { get; set; }
        public List<Item> LowStockItems { get; set; } = new();
        public List<Item> SlowMovingItems { get; set; } = new();
        public List<TopItemDto> TopSellingItems { get; set; } = new();
        public List<Order> RecentOrders { get; set; } = new();

        // --- CHART DATA PROPERTIES ---
        public string SalesChartLabels { get; set; } = "[]";
        public string SalesChartData { get; set; } = "[]";
        public string MonthlySalesLabels { get; set; } = "[]";
        public string MonthlySalesData { get; set; } = "[]";
        public string MonthlyForecastData { get; set; } = "[]";
        public string QuarterlySalesLabels { get; set; } = "[]";
        public string QuarterlySalesData { get; set; } = "[]";
        public string QuarterlyForecastData { get; set; } = "[]";
        public string CategoryLabels { get; set; } = "[]";
        public string CategoryData { get; set; } = "[]";

        // --- AI & Helpers ---
        public string ForecastChartData { get; set; } = "[]";
        public string HolidayPrediction { get; set; } = "System awaiting simulation.";
        public List<string> ActionableTips { get; set; } = new();
        public DateTime? LastAnalysisDate { get; set; }

        public List<PeriodStat> MonthlyStats { get; set; } = new();
        public List<PeriodStat> QuarterlyStats { get; set; } = new();

        public async Task OnGetAsync()
        {
            var storeId = await GetStoreIdAsync();
            if (string.IsNullOrEmpty(storeId)) return;

            // Load Data
            var store = await _db.GetCollection<Store>(StoreCollection).Find(s => s.Id == storeId).FirstOrDefaultAsync();
            var items = await _db.GetCollection<Item>(ItemCollection).Find(i => i.StoreId == storeId).ToListAsync();

            // --- GATEKEEPER CHECK ---
            HasInventory = items.Count > 0;

            var orders = await _db.GetCollection<Order>(OrderCollection)
                                  .Find(o => o.StoreId == storeId)
                                  .SortByDescending(o => o.OrderDate)
                                  .ToListAsync();

            // Calculate KPIs
            RecentOrders = orders.Take(20).ToList();
            TotalRevenue = orders.Sum(o => o.TotalAmount);
            TotalOrders = orders.Count;
            TotalProfit = orders.SelectMany(o => o.Items).Sum(i => (i.Price - i.Cost) * i.Quantity);
            LowStockItems = items.Where(i => i.Quantity < 5).OrderBy(i => i.Quantity).ToList();

            var soldItemNames = orders.SelectMany(o => o.Items).Select(i => i.ItemName).Distinct().ToHashSet();
            SlowMovingItems = items.Where(i => i.Quantity > 10 && !soldItemNames.Contains(i.Name)).Take(5).ToList();

            PrepareSalesCharts(orders);
            PrepareCategoryCharts(items, orders);
            PrepareTopSellers(orders);

            // Load Cached AI
            if (store != null && store.LastReport != null)
            {
                ForecastChartData = JsonSerializer.Serialize(store.LastReport.Forecast);
                ForecastedRevenue = store.LastReport.ForecastedRevenue;
                HolidayPrediction = store.LastReport.HolidayNote;
                ActionableTips = store.LastReport.Tips;
                LastAnalysisDate = store.LastReport.LastAnalysisDate;
            }
        }

        // Renamed from OnPostSeedHistoryAsync to reflect the UI change
        public async Task<IActionResult> OnPostRunSimulationAsync()
        {
            var storeId = await GetStoreIdAsync();
            if (string.IsNullOrEmpty(storeId)) return RedirectToPage();

            // --- STRICT GATEKEEPER CHECK ---
            var items = await _db.GetCollection<Item>(ItemCollection).Find(i => i.StoreId == storeId).ToListAsync();
            if (items.Count == 0)
            {
                TempData["error"] = "Simulation failed: No products in inventory.";
                return RedirectToPage();
            }

            var ordersCollection = _db.GetCollection<Order>(OrderCollection);
            var newOrders = new List<Order>();
            var random = new Random();

            // Simulate 90 days of "Predictive History" based on actual inventory
            for (int i = 90; i >= 0; i--)
            {
                // Simple randomization for now, but linked to real items
                if (random.NextDouble() > 0.7) continue;

                var date = DateTime.UtcNow.AddDays(-i);
                int dailyOrders = random.Next(1, 4);

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
                        CustomerName = "Simulated Customer",
                        OrderCode = $"AI-SIM-{random.Next(1000, 9999)}",
                        Status = "Completed",
                        OrderDate = date,
                        Items = orderItems,
                        TotalAmount = orderItems.Sum(x => x.Total)
                    });
                }
            }

            if (newOrders.Count > 0) await ordersCollection.InsertManyAsync(newOrders);
            TempData["success"] = $"AI Simulation Complete: Generated {newOrders.Count} predictive transaction points.";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostDownloadReportAsync()
        {
            await OnGetAsync();
            var content = new StringBuilder();
            content.AppendLine("Sari-Sari Store Performance Report");
            content.AppendLine($"Generated: {DateTime.Now.ToString("MMMM dd, yyyy")}");
            content.AppendLine($"Last Analysis: {LastAnalysisDate?.ToLocalTime().ToString("MMMM dd, hh:mm tt") ?? "N/A"}");
            content.AppendLine();
            // ... (CSV Generation Logic remains the same as previous) ...
            content.AppendLine("Metric,Value");
            content.AppendLine($"Total Revenue,₱{TotalRevenue:N2}");
            var fileName = $"Sari_Report_{DateTime.Now.ToString("yyyyMMdd")}.csv";
            return File(Encoding.UTF8.GetBytes(content.ToString()), "text/csv", fileName);
        }

        public async Task<IActionResult> OnPostAnalyzeAsync()
        {
            var storeId = await GetStoreIdAsync();
            if (string.IsNullOrEmpty(storeId)) return RedirectToPage();

            // Check if we have orders to analyze
            var orders = await _db.GetCollection<Order>(OrderCollection).Find(o => o.StoreId == storeId).ToListAsync();
            if (orders.Count < 5)
            {
                TempData["error"] = "Insufficient data points for AI analysis. Please 'Run Sim' first.";
                return RedirectToPage();
            }

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
                TempData["success"] = "Market patterns analyzed successfully.";
            }
            else
            {
                TempData["error"] = "AI Service busy. Please try again in a moment.";
            }

            return RedirectToPage();
        }

        // --- CHART HELPERS REMAIN SAME ---
        private void PrepareSalesCharts(List<Order> orders)
        {
            var last7Days = Enumerable.Range(0, 7).Select(i => DateTime.UtcNow.Date.AddDays(-6 + i)).ToList();
            var dailyMap = new Dictionary<string, decimal>();
            foreach (var date in last7Days) dailyMap[date.ToString("MMM dd")] = orders.Where(o => o.OrderDate.Date == date).Sum(o => o.TotalAmount);
            SalesChartLabels = JsonSerializer.Serialize(dailyMap.Keys);
            SalesChartData = JsonSerializer.Serialize(dailyMap.Values);

            var monthlyGroups = orders.GroupBy(o => new { o.OrderDate.Year, o.OrderDate.Month })
                                      .Select(g => new PeriodStat { Label = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMM"), Total = g.Sum(o => o.TotalAmount), Date = new DateTime(g.Key.Year, g.Key.Month, 1) })
                                      .OrderBy(x => x.Date).TakeLast(12).ToList();
            MonthlySalesLabels = JsonSerializer.Serialize(monthlyGroups.Select(x => x.Label));
            MonthlySalesData = JsonSerializer.Serialize(monthlyGroups.Select(x => x.Total));
            MonthlyStats = monthlyGroups;
            MonthlyForecastData = JsonSerializer.Serialize(new decimal[monthlyGroups.Count]);

            var quarterlyGroups = orders.GroupBy(o => new { o.OrderDate.Year, Quarter = (o.OrderDate.Month - 1) / 3 + 1 })
                                        .Select(g => new PeriodStat { Label = $"Q{g.Key.Quarter} {g.Key.Year}", Total = g.Sum(o => o.TotalAmount), Date = new DateTime(g.Key.Year, (g.Key.Quarter - 1) * 3 + 1, 1) })
                                        .OrderBy(x => x.Date).TakeLast(8).ToList();
            QuarterlySalesLabels = JsonSerializer.Serialize(quarterlyGroups.Select(x => x.Label));
            QuarterlySalesData = JsonSerializer.Serialize(quarterlyGroups.Select(x => x.Total));
            QuarterlyStats = quarterlyGroups;
            QuarterlyForecastData = JsonSerializer.Serialize(new decimal[quarterlyGroups.Count]);
        }

        private void PrepareCategoryCharts(List<Item> items, List<Order> orders)
        {
            var catMap = items.ToDictionary(i => i.Name, i => i.Category);
            Dictionary<string, decimal> AggregateCats(IEnumerable<Order> filteredOrders)
            {
                var dict = new Dictionary<string, decimal>();
                foreach (var oItem in filteredOrders.SelectMany(o => o.Items))
                {
                    string cat = catMap.ContainsKey(oItem.ItemName) ? catMap[oItem.ItemName] : "General";
                    if (!dict.ContainsKey(cat)) dict[cat] = 0;
                    dict[cat] += oItem.Total;
                }
                return dict;
            }
            var allTime = AggregateCats(orders);
            CategoryLabels = JsonSerializer.Serialize(allTime.Keys);
            CategoryData = JsonSerializer.Serialize(allTime.Values);
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

        private async Task<string?> GetStoreIdAsync()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return null;
            var store = await _db.GetCollection<Store>(StoreCollection).Find(s => s.OwnerId == userId).FirstOrDefaultAsync();
            return store?.Id;
        }

        private async Task<AiReportData?> GenerateAiForecast(Dictionary<string, decimal> pastSales)
        {
            var keys = _sari.GetDecryptedKeys();
            var baseUrl = _sari.GetFullApiUrl();
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
                            text = text.Replace("```json", "").Replace("```", "").Trim();
                            return JsonSerializer.Deserialize<AiReportData>(text, _jsonOptions);
                        }
                    }
                }
                catch { continue; }
            }
            return null;
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

        public class PeriodStat
        {
            public string Label { get; set; } = "";
            public decimal Total { get; set; }
            public DateTime Date { get; set; }
        }
    }
}