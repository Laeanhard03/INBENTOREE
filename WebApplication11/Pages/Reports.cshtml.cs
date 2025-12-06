using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MongoDB.Driver;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using System.Net.Http;
using System.Text;
using System.Security.Claims;
using WebApplication11.Services;
using System.IO; // <--- ADDED: Required for File result

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
        // 1. Sales Trends
        public string SalesChartLabels { get; set; } = "[]";      // Daily
        public string SalesChartData { get; set; } = "[]";
        public string MonthlySalesLabels { get; set; } = "[]";    // Monthly
        public string MonthlySalesData { get; set; } = "[]";
        public string QuarterlySalesLabels { get; set; } = "[]";  // Quarterly
        public string QuarterlySalesData { get; set; } = "[]";

        // 2. Category Breakdowns
        public string CategoryLabels { get; set; } = "[]";        // All Time
        public string CategoryData { get; set; } = "[]";
        public string CategoryMonthlyLabels { get; set; } = "[]"; // This Month
        public string CategoryMonthlyData { get; set; } = "[]";
        public string CategoryQuarterlyLabels { get; set; } = "[]"; // This Quarter
        public string CategoryQuarterlyData { get; set; } = "[]";

        // 3. AI & Helpers
        public string ForecastChartData { get; set; } = "[]";
        public string HolidayPrediction { get; set; } = "Click 'Re-Analyze' to generate AI insights.";
        public List<string> ActionableTips { get; set; } = new();
        public DateTime? LastAnalysisDate { get; set; }

        // --- APPENDIX DATA FOR PDF (Tables) ---
        public List<PeriodStat> MonthlyStats { get; set; } = new();
        public List<PeriodStat> QuarterlyStats { get; set; } = new();

        public async Task OnGetAsync()
        {
            var storeId = await GetStoreIdAsync();
            if (string.IsNullOrEmpty(storeId)) return;

            // Load Data
            var store = await _db.GetCollection<Store>(StoreCollection).Find(s => s.Id == storeId).FirstOrDefaultAsync();
            var items = await _db.GetCollection<Item>(ItemCollection).Find(i => i.StoreId == storeId).ToListAsync();
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

            // --- PREPARE CHARTS & REPORTS ---
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

        // <--- NEW HANDLER FOR DIRECT DOWNLOAD --->
        public async Task<IActionResult> OnPostDownloadReportAsync()
        {
            // Ensure all data is loaded before generating the file content
            await OnGetAsync();

            var content = new StringBuilder();
            content.AppendLine("Sari-Sari Store Performance Report");
            content.AppendLine($"Generated: {DateTime.Now.ToString("MMMM dd, yyyy")}");
            content.AppendLine($"Last Analysis: {LastAnalysisDate?.ToLocalTime().ToString("MMMM dd, hh:mm tt") ?? "N/A"}");
            content.AppendLine();
            content.AppendLine("--- KEY PERFORMANCE INDICATORS ---");
            content.AppendLine($"Total Revenue,₱{TotalRevenue:N2}");
            content.AppendLine($"Net Profit,₱{TotalProfit:N2}");
            content.AppendLine($"AI Forecast (7 Days),₱{ForecastedRevenue:N2}");
            content.AppendLine($"Restock Needed,{LowStockItems.Count}");
            content.AppendLine();

            // AI Analysis (Text)
            content.AppendLine("--- SARI'S INTELLIGENT ANALYSIS ---");
            content.AppendLine("Key Event/Holiday Prediction:");
            content.AppendLine(HolidayPrediction);
            content.AppendLine("Recommended Action Plan:");
            foreach (var tip in ActionableTips)
            {
                content.AppendLine($"- {tip}");
            }
            content.AppendLine();

            // Top Selling Items Table
            content.AppendLine("--- TOP PERFORMERS (Quantity Sold) ---");
            content.AppendLine("Product,Quantity Sold,Total Revenue");
            foreach (var item in TopSellingItems)
            {
                content.AppendLine($"{item.Name},{item.QuantitySold},₱{item.TotalRevenue:N0}");
            }
            content.AppendLine();

            // Slow Moving Items Table
            content.AppendLine("--- SLOW MOVING ITEMS (Over 10 stock, 0 recent sales) ---");
            content.AppendLine("Item,Current Stock");
            foreach (var item in SlowMovingItems)
            {
                content.AppendLine($"{item.Name},{item.Quantity}");
            }
            content.AppendLine();


            // Monthly Stats Table (from Appendix)
            content.AppendLine("--- MONTHLY PERFORMANCE (Last 12 Months) ---");
            content.AppendLine("Month,Revenue");
            foreach (var m in MonthlyStats)
            {
                content.AppendLine($"{m.Label},₱{m.Total:N2}");
            }
            content.AppendLine();

            // Quarterly Stats Table (from Appendix)
            content.AppendLine("--- QUARTERLY PERFORMANCE (Last 8 Quarters) ---");
            content.AppendLine("Quarter,Revenue");
            foreach (var q in QuarterlyStats)
            {
                content.AppendLine($"{q.Label},₱{q.Total:N2}");
            }

            var fileName = $"Sari_Report_{DateTime.Now.ToString("yyyyMMdd")}.csv";
            var fileBytes = Encoding.UTF8.GetBytes(content.ToString());

            // Return the file for direct download
            return File(fileBytes, "text/csv", fileName);
        }
        // <--- END NEW HANDLER --->


        // --- EXISTING HELPER METHODS (PrepareSalesCharts, PrepareCategoryCharts, PrepareTopSellers, etc.) ---
        private void PrepareSalesCharts(List<Order> orders)
        {
            // 1. Daily (Last 7 Days)
            var last7Days = Enumerable.Range(0, 7).Select(i => DateTime.UtcNow.Date.AddDays(-6 + i)).ToList();
            var dailyMap = new Dictionary<string, decimal>();
            foreach (var date in last7Days) dailyMap[date.ToString("MMM dd")] = orders.Where(o => o.OrderDate.Date == date).Sum(o => o.TotalAmount);
            SalesChartLabels = JsonSerializer.Serialize(dailyMap.Keys);
            SalesChartData = JsonSerializer.Serialize(dailyMap.Values);

            // 2. Monthly (Last 12 Months)
            var monthlyGroups = orders.GroupBy(o => new { o.OrderDate.Year, o.OrderDate.Month })
                                      .Select(g => new PeriodStat { Label = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMM yyyy"), Total = g.Sum(o => o.TotalAmount), Date = new DateTime(g.Key.Year, g.Key.Month, 1) })
                                      .OrderBy(x => x.Date).TakeLast(12).ToList();
            MonthlySalesLabels = JsonSerializer.Serialize(monthlyGroups.Select(x => x.Label));
            MonthlySalesData = JsonSerializer.Serialize(monthlyGroups.Select(x => x.Total));
            MonthlyStats = monthlyGroups; // For PDF Table

            // 3. Quarterly (Last 8 Quarters)
            var quarterlyGroups = orders.GroupBy(o => new { o.OrderDate.Year, Quarter = (o.OrderDate.Month - 1) / 3 + 1 })
                                        .Select(g => new PeriodStat { Label = $"Q{g.Key.Quarter} {g.Key.Year}", Total = g.Sum(o => o.TotalAmount), Date = new DateTime(g.Key.Year, (g.Key.Quarter - 1) * 3 + 1, 1) })
                                        .OrderBy(x => x.Date).TakeLast(8).ToList();
            QuarterlySalesLabels = JsonSerializer.Serialize(quarterlyGroups.Select(x => x.Label));
            QuarterlySalesData = JsonSerializer.Serialize(quarterlyGroups.Select(x => x.Total));
            QuarterlyStats = quarterlyGroups; // For PDF Table
        }

        private void PrepareCategoryCharts(List<Item> items, List<Order> orders)
        {
            var catMap = items.ToDictionary(i => i.Name, i => i.Category);

            // Helper to aggregate
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

            // 1. All Time
            var allTime = AggregateCats(orders);
            CategoryLabels = JsonSerializer.Serialize(allTime.Keys);
            CategoryData = JsonSerializer.Serialize(allTime.Values);

            // 2. This Month
            var thisMonth = AggregateCats(orders.Where(o => o.OrderDate.Month == DateTime.UtcNow.Month && o.OrderDate.Year == DateTime.UtcNow.Year));
            CategoryMonthlyLabels = JsonSerializer.Serialize(thisMonth.Keys);
            CategoryMonthlyData = JsonSerializer.Serialize(thisMonth.Values);

            // 3. This Quarter
            int currentQ = (DateTime.UtcNow.Month - 1) / 3 + 1;
            var thisQuarter = AggregateCats(orders.Where(o => ((o.OrderDate.Month - 1) / 3 + 1) == currentQ && o.OrderDate.Year == DateTime.UtcNow.Year));
            CategoryQuarterlyLabels = JsonSerializer.Serialize(thisQuarter.Keys);
            CategoryQuarterlyData = JsonSerializer.Serialize(thisQuarter.Values);
        }

        public async Task<IActionResult> OnPostAnalyzeAsync()
        {
            var storeId = await GetStoreIdAsync();
            if (string.IsNullOrEmpty(storeId)) return RedirectToPage();

            var orders = await _db.GetCollection<Order>(OrderCollection).Find(o => o.StoreId == storeId).ToListAsync();

            if (orders.Count < 5)
            {
                TempData["error"] = "Not enough data! Please click 'Gen Data' to create sample sales history first.";
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
                TempData["success"] = "AI Analysis Complete!";
            }
            else
            {
                TempData["error"] = "AI Service busy. Please try again in a moment.";
            }

            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostSeedHistoryAsync()
        {
            var storeId = await GetStoreIdAsync();
            if (string.IsNullOrEmpty(storeId)) return RedirectToPage();

            var items = await _db.GetCollection<Item>(ItemCollection).Find(i => i.StoreId == storeId).ToListAsync();
            if (items.Count == 0) return RedirectToPage();

            var ordersCollection = _db.GetCollection<Order>(OrderCollection);
            var newOrders = new List<Order>();
            var random = new Random();

            // Create fake sales for the last 90 days to populate quarters
            for (int i = 90; i >= 0; i--)
            {
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