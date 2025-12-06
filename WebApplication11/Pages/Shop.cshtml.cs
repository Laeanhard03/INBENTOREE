using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MongoDB.Driver;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using WebApplication11.Services;

namespace WebApplication11.Pages
{
    [AllowAnonymous]
    public class ShopModel : PageModel
    {
        private readonly IMongoDatabase _db;
        private readonly IConfiguration _config;
        private readonly SariService _sari;

        private const string CollectionName = "Items";
        private const string StoreCollectionName = "Stores";
        private const string ChatCollectionName = "Chats";
        private const string NotifCollectionName = "Notifications";

        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        public ShopModel(IMongoDatabase db, IConfiguration config, SariService sari)
        {
            _db = db;
            _config = config;
            _sari = sari;
        }

        public Store? StoreInfo { get; set; }
        public string CurrentView { get; set; } = "shop";
        public List<Item> Products { get; set; } = new();
        public List<Store> MarketplaceStores { get; set; } = new();

        [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }
        [BindProperty(SupportsGet = true)] public string? SortOrder { get; set; }
        [BindProperty(SupportsGet = true)] public decimal? MinPrice { get; set; }
        [BindProperty(SupportsGet = true)] public decimal? MaxPrice { get; set; }
        [BindProperty(SupportsGet = true)] public string? StoreId { get; set; }

        public List<CartItemDetail> CartItems { get; set; } = new();
        public decimal GrandTotal { get; set; }
        public Order? ReceiptOrder { get; set; }

        // --- NEW: AUTO-COMPLETE HANDLER ---
        public async Task<IActionResult> OnGetSearchSuggestionsAsync(string term, string? storeId)
        {
            if (string.IsNullOrWhiteSpace(term)) return new JsonResult(new List<string>());

            var builder = Builders<Item>.Filter;
            var filter = builder.Regex("Name", new MongoDB.Bson.BsonRegularExpression($"^{term}", "i")); // Starts with

            // If inside a specific store, scope search. If in marketplace, search all.
            if (!string.IsNullOrEmpty(storeId) && !storeId.StartsWith("mock_"))
            {
                filter &= builder.Eq(i => i.StoreId, storeId);
            }

            var items = await _db.GetCollection<Item>(CollectionName)
                                 .Find(filter)
                                 .Limit(5)
                                 .Project(i => i.Name)
                                 .ToListAsync();

            return new JsonResult(items);
        }

        public async Task<IActionResult> OnGetAsync(string? view = "shop", string? orderId = null)
        {
            // --- 1. HANDLE SEARCH VIEW FIRST ---
            if (!string.IsNullOrEmpty(SearchTerm))
            {
                CurrentView = "search_results";
                var builder = Builders<Item>.Filter;
                var regex = new MongoDB.Bson.BsonRegularExpression(SearchTerm, "i");
                var filter = builder.Or(builder.Regex("Name", regex), builder.Regex("Category", regex));

                // Scoped Search: If user is inside a specific store, only search there
                if (!string.IsNullOrEmpty(StoreId) && !StoreId.StartsWith("mock_"))
                {
                    filter &= builder.Eq(i => i.StoreId, StoreId);
                }

                Products = await _db.GetCollection<Item>(CollectionName).Find(filter).ToListAsync();

                // Load Store Info if we are inside a store context
                if (!string.IsNullOrEmpty(StoreId) && !StoreId.StartsWith("mock_"))
                {
                    StoreInfo = await _db.GetCollection<Store>(StoreCollectionName).Find(s => s.Id == StoreId).FirstOrDefaultAsync();
                }

                return Page();
            }

            // --- 2. MARKETPLACE VIEW (No Store Selected) ---
            if (string.IsNullOrEmpty(StoreId))
            {
                CurrentView = "marketplace";
                var storeCollection = _db.GetCollection<Store>(StoreCollectionName);

                // Add Mock Stores
                MarketplaceStores.Add(new Store { Id = "mock_1", StoreName = "Aling Nena's Store", Description = "Philippines", ThemeColor = "#ea580c" });
                MarketplaceStores.Add(new Store { Id = "mock_2", StoreName = "Metro Mart Finds", Description = "Philippines", ThemeColor = "#16a34a" });
                MarketplaceStores.Add(new Store { Id = "mock_3", StoreName = "Daily Needs Deals", Description = "Philippines", ThemeColor = "#2563eb" });
                MarketplaceStores.Add(new Store { Id = "mock_4", StoreName = "Chahod Stores", Description = "Philippines", ThemeColor = "#d97706" });
                MarketplaceStores.Add(new Store { Id = "mock_5", StoreName = "Rabmat Stores", Description = "Philippines", ThemeColor = "#ca8a04" });

                // FETCH LOGGED IN USER'S STORE (Real DB)
                if (User.Identity?.IsAuthenticated == true)
                {
                    try
                    {
                        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                        if (!string.IsNullOrEmpty(userId))
                        {
                            var myStore = await storeCollection.Find(s => s.OwnerId == userId).FirstOrDefaultAsync();
                            if (myStore != null) MarketplaceStores.Insert(0, myStore);
                        }
                    }
                    catch { }
                }

                return Page();
            }

            // --- 3. SPECIFIC STORE VIEW ---
            if (StoreId.StartsWith("mock_"))
            {
                if (StoreId == "mock_1") StoreInfo = new Store { Id = "mock_1", StoreName = "Aling Nena's Store", ThemeColor = "#ea580c", Description = "Philippines" };
                else if (StoreId == "mock_2") StoreInfo = new Store { Id = "mock_2", StoreName = "Metro Mart Finds", ThemeColor = "#16a34a", Description = "Philippines" };
                else if (StoreId == "mock_3") StoreInfo = new Store { Id = "mock_3", StoreName = "Daily Needs Deals", ThemeColor = "#2563eb", Description = "Philippines" };
                else if (StoreId == "mock_4") StoreInfo = new Store { Id = "mock_4", StoreName = "Chahod Stores", ThemeColor = "#d97706", Description = "Philippines" };
                else if (StoreId == "mock_5") StoreInfo = new Store { Id = "mock_5", StoreName = "Rabmat Stores", ThemeColor = "#ca8a04", Description = "Philippines" };
                else StoreInfo = new Store { Id = StoreId, StoreName = "Mock Store", Description = "Test Mode" };
            }
            else
            {
                var specificStoreCollection = _db.GetCollection<Store>(StoreCollectionName);
                try { StoreInfo = await specificStoreCollection.Find(s => s.Id == StoreId).FirstOrDefaultAsync(); }
                catch { return NotFound("Invalid Store ID."); }
            }

            if (StoreInfo == null) return NotFound("Store not found.");

            CurrentView = view ?? "shop";

            if (CurrentView == "cart")
            {
                await LoadCartData();
            }
            else if (CurrentView == "receipt" && !string.IsNullOrEmpty(orderId))
            {
                if (orderId.StartsWith("MOCK"))
                {
                    ReceiptOrder = new Order { Id = orderId, OrderCode = orderId, TotalAmount = 100, Items = new List<CartItemDetail>() };
                }
                else
                {
                    ReceiptOrder = await _db.GetCollection<Order>("Orders").Find(o => o.Id == orderId).FirstOrDefaultAsync();
                }
                if (ReceiptOrder == null) CurrentView = "shop";
            }
            else
            {
                // Load Products
                if (StoreId.StartsWith("mock_"))
                {
                    Products = GenerateMockProducts(StoreId);
                }
                else
                {
                    var builder = Builders<Item>.Filter;
                    var filter = builder.Eq(i => i.StoreId, StoreId);

                    if (MinPrice.HasValue) filter &= builder.Gte(i => i.Price, MinPrice.Value);
                    if (MaxPrice.HasValue) filter &= builder.Lte(i => i.Price, MaxPrice.Value);

                    var sort = Builders<Item>.Sort.Ascending(i => i.Position);
                    if (SortOrder == "price_asc") sort = Builders<Item>.Sort.Ascending(i => i.Price);
                    else if (SortOrder == "price_desc") sort = Builders<Item>.Sort.Descending(i => i.Price);
                    else if (SortOrder == "name") sort = Builders<Item>.Sort.Ascending(i => i.Name);

                    Products = await _db.GetCollection<Item>(CollectionName).Find(filter).Sort(sort).ToListAsync();
                }
            }
            return Page();
        }

        private static List<Item> GenerateMockProducts(string storeId)
        {
            var list = new List<Item>();
            list.Add(new Item { Id = "p1", Name = "Canned Goods (Del Monte)", Category = "Canned Goods", Price = 10.00m, Quantity = 50 });
            list.Add(new Item { Id = "p2", Name = "Nano-Bite Snacks (Lays)", Category = "Snacks", Price = 12.00m, Quantity = 20 });
            list.Add(new Item { Id = "p3", Name = "Century Tuna", Category = "Canned Goods", Price = 18.00m, Quantity = 100 });
            list.Add(new Item { Id = "p4", Name = "SPAM", Category = "Canned Goods", Price = 24.00m, Quantity = 15 });
            list.Add(new Item { Id = "p5", Name = "Bitte Mins", Category = "Snacks", Price = 8.00m, Quantity = 30 });
            list.Add(new Item { Id = "p6", Name = "Skyflakes", Category = "Snacks", Price = 15.00m, Quantity = 60 });
            list.Add(new Item { Id = "p7", Name = "Coke Can", Category = "Drinks", Price = 11.50m, Quantity = 40 });
            list.Add(new Item { Id = "p8", Name = "Lays Classic", Category = "Snacks", Price = 19.00m, Quantity = 25 });
            return list;
        }

        public async Task<IActionResult> OnPostAddToCartAsync(string itemId, int quantity)
        {
            var session = HttpContext.Session;
            string cartJson = session.GetString("Cart") ?? "[]";
            var cart = JsonSerializer.Deserialize<List<CartItem>>(cartJson) ?? new List<CartItem>();
            if (quantity < 1) quantity = 1;

            var existingItem = cart.FirstOrDefault(c => c.ItemId == itemId);
            if (existingItem != null) existingItem.Quantity += quantity;
            else cart.Add(new CartItem { ItemId = itemId, Quantity = quantity });

            session.SetString("Cart", JsonSerializer.Serialize(cart));

            if (!itemId.StartsWith("p") && !itemId.StartsWith("m"))
            {
                try
                {
                    var item = await _db.GetCollection<Item>(CollectionName).Find(i => i.Id == itemId).FirstOrDefaultAsync();
                    if (item != null && !string.IsNullOrEmpty(item.StoreId))
                        await CreateNotification(item.StoreId, $"Customer added {quantity}x {item.Name} to cart.", "cart");
                }
                catch { }
            }
            return new JsonResult(new { success = true, count = cart.Sum(c => c.Quantity) });
        }

        public async Task<IActionResult> OnPostCheckoutAsync()
        {
            await LoadCartData();
            if (CartItems.Count == 0) return RedirectToPage(new { StoreId, view = "shop" });

            if (StoreId != null && StoreId.StartsWith("mock_"))
            {
                var sessionMock = HttpContext.Session;
                sessionMock.Remove("Cart");
                return RedirectToPage(new { StoreId, view = "receipt", orderId = "MOCK-ORDER-" + new Random().Next(100, 999) });
            }

            var itemsCollection = _db.GetCollection<Item>("Items");
            var ordersCollection = _db.GetCollection<Order>("Orders");
            var session = HttpContext.Session;
            string cartJson = session.GetString("Cart") ?? "[]";
            var cartSession = JsonSerializer.Deserialize<List<CartItem>>(cartJson) ?? new List<CartItem>();

            foreach (var c in cartSession)
            {
                try
                {
                    var item = await itemsCollection.Find(i => i.Id == c.ItemId).FirstOrDefaultAsync();
                    if (item != null)
                    {
                        var update = Builders<Item>.Update.Inc(i => i.Quantity, -c.Quantity);
                        await itemsCollection.UpdateOneAsync(i => i.Id == item.Id, update);
                    }
                }
                catch { }
            }

            var newOrder = new Order
            {
                StoreId = StoreId!,
                CustomerName = User.Identity?.Name ?? "Guest",
                Items = CartItems,
                TotalAmount = GrandTotal,
                OrderCode = "OR-" + new Random().Next(1000, 9999),
                Status = "Pending"
            };

            await ordersCollection.InsertOneAsync(newOrder);
            session.Remove("Cart");
            await CreateNotification(StoreId!, $"New Order {newOrder.OrderCode} received! Total: ₱{GrandTotal:N2}", "order");
            return RedirectToPage(new { StoreId, view = "receipt", orderId = newOrder.Id });
        }

        private async Task LoadCartData()
        {
            var session = HttpContext.Session;
            string cartJson = session.GetString("Cart") ?? "[]";
            var cartSession = JsonSerializer.Deserialize<List<CartItem>>(cartJson) ?? new List<CartItem>();
            CartItems = new List<CartItemDetail>();

            if (StoreId != null && StoreId.StartsWith("mock_"))
            {
                foreach (var c in cartSession)
                {
                    var products = GenerateMockProducts(StoreId);
                    var p = products.FirstOrDefault(x => x.Id == c.ItemId);
                    if (p != null) CartItems.Add(new CartItemDetail { ItemName = p.Name, Price = p.Price, Quantity = c.Quantity });
                }
            }
            else
            {
                var itemsCollection = _db.GetCollection<Item>("Items");
                foreach (var c in cartSession)
                {
                    try
                    {
                        var dbItem = await itemsCollection.Find(i => i.Id == c.ItemId).FirstOrDefaultAsync();
                        if (dbItem != null) CartItems.Add(new CartItemDetail { ItemName = dbItem.Name, Price = dbItem.Price, Cost = dbItem.CostPrice, Quantity = c.Quantity });
                    }
                    catch { }
                }
            }
            GrandTotal = CartItems.Sum(x => x.Total);
        }

        private async Task CreateNotification(string storeId, string msg, string type)
        {
            var notif = new Notification { StoreId = storeId, Message = msg, Type = type, IsRead = false, Timestamp = DateTime.UtcNow };
            await _db.GetCollection<Notification>(NotifCollectionName).InsertOneAsync(notif);
        }

        public async Task<IActionResult> OnPostSendUserMessageAsync(string storeId, string guestId, string content)
        {
            var msg = new ChatMessage { StoreId = storeId, GuestId = guestId, Sender = "User", Content = content, Timestamp = DateTime.UtcNow };
            await _db.GetCollection<ChatMessage>(ChatCollectionName).InsertOneAsync(msg);
            await CreateNotification(storeId, "New message from customer.", "chat");
            return new JsonResult(new { success = true });
        }

        public async Task<IActionResult> OnGetCheckMessagesAsync(string storeId, string guestId)
        {
            var chats = await _db.GetCollection<ChatMessage>(ChatCollectionName).Find(c => c.StoreId == storeId && c.GuestId == guestId).SortBy(c => c.Timestamp).ToListAsync();
            return new JsonResult(new { messages = chats });
        }

        public async Task<IActionResult> OnPostAiChatAsync(string input, string storeId, string guestId)
        {
            string systemPrompt = "You are Sari, a helpful store assistant. Return ONLY raw JSON: { \"handoff\": boolean, \"reply\": \"string\" }";
            string finalPrompt = $"{systemPrompt}\nUser: {input}";
            string rawAiResponse = await CallSariService(finalPrompt);
            rawAiResponse = rawAiResponse.Replace("```json", "").Replace("```", "").Trim();
            bool handoff = false;
            string reply = "Sorry, I didn't quite get that.";
            try
            {
                var responseObj = JsonSerializer.Deserialize<AiChatResponse>(rawAiResponse, _jsonOptions);
                if (responseObj != null) { handoff = responseObj.Handoff; reply = responseObj.Reply; }
            }
            catch { reply = "Connecting you to the seller..."; handoff = true; }

            if (handoff)
            {
                var collection = _db.GetCollection<ChatMessage>(ChatCollectionName);
                await collection.InsertOneAsync(new ChatMessage { StoreId = storeId, GuestId = guestId, Sender = "User", Content = input, Timestamp = DateTime.UtcNow });
                await collection.InsertOneAsync(new ChatMessage { StoreId = storeId, GuestId = guestId, Sender = "Sari (AI)", Content = reply, Timestamp = DateTime.UtcNow.AddMilliseconds(500) });
                await CreateNotification(storeId, "Customer requested human assistance.", "chat");
            }
            return new JsonResult(new { reply = reply, handoff = handoff });
        }

        private async Task<string> CallSariService(string prompt)
        {
            var keys = _sari.GetDecryptedKeys();
            string baseUrl = _sari.GetFullApiUrl();
            using var client = new HttpClient();
            var content = new StringContent(JsonSerializer.Serialize(new { contents = new[] { new { parts = new[] { new { text = prompt } } } } }), Encoding.UTF8, "application/json");

            foreach (var key in keys)
            {
                try
                {
                    var response = await client.PostAsync($"{baseUrl}?key={key}", content);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                            return candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "{}";
                    }
                }
                catch { continue; }
            }
            return "{}";
        }
        public class AiChatResponse { public bool Handoff { get; set; } public string Reply { get; set; } = string.Empty; }
    }
}