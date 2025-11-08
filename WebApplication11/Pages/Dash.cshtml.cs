using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MongoDB.Driver;
using MongoDB.Bson.Serialization.Attributes;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using System.Linq;
using Microsoft.AspNetCore.Authorization; // For [Authorize]
using Microsoft.AspNetCore.Authentication; // For HttpContext.SignOutAsync
using Microsoft.AspNetCore.Authentication.Cookies; // For CookieAuthenticationDefaults
using Microsoft.Extensions.Logging; // For ILogger

namespace WebApplication11.Pages;

[Authorize] // CRITICAL: This protects the page, only logged-in users can access it
public class DashModel : PageModel
{
    private readonly IMongoDatabase _db;
    private const string CollectionName = "Items";
    private readonly ILogger<DashModel> _logger; // For logging

    // Constructor to receive database and logger services
    public DashModel(IMongoDatabase db, ILogger<DashModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    // --- Page Data ---
    public List<Item> Items { get; set; } = [];

    // --- Bind Properties for Forms ---
    [BindProperty] public Item NewItem { get; set; } = new();
    [BindProperty] public IFormFile? LogoFile { get; set; }
    [BindProperty] public Item EditItem { get; set; } = new();
    [BindProperty] public string? DeleteId { get; set; }
    [BindProperty] public string[]? ItemsToSwitch { get; set; }
    [BindProperty] public string? ItemsToDelete { get; set; }

    // --- Page Handlers ---

    /// <summary>
    /// Runs when the page is loaded. Fetches all inventory items.
    /// </summary>
    public async Task OnGetAsync()
    {
        // Log that the user has successfully accessed their dashboard
        _logger.LogInformation("User {Username} successfully accessed the dashboard.", User.Identity?.Name ?? "Unknown");

        // R (Read): Fetches all documents, sorted by Position.
        Items = await _db.GetCollection<Item>(CollectionName)
                             .Find(_ => true)
                             .SortBy(item => item.Position) // Sort by Position
                             .ToListAsync();
    }

    /// <summary>
    /// Handles the user logging out.
    /// </summary>
    public async Task<IActionResult> OnPostLogoutAsync()
    {
        _logger.LogInformation("User {Username} is logging out.", User.Identity?.Name ?? "Unknown");
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        TempData["success"] = "You have been logged out."; // Notification for the login page
        return RedirectToPage("/Index");
    }

    /// <summary>
    /// Handles creating a new inventory item.
    /// </summary>
    public async Task<IActionResult> OnPostAddAsync()
    {
        if (LogoFile != null && LogoFile.Length > 0)
        {
            NewItem.LogoContentType = LogoFile.ContentType;
            using var memoryStream = new MemoryStream();
            await LogoFile.CopyToAsync(memoryStream);
            NewItem.LogoData = memoryStream.ToArray();
        }
        else
        {
            NewItem.LogoContentType = null;
            NewItem.LogoData = null;
        }

        // Assign Position: The new item gets the highest existing position + 1.
        var highestPositionItem = await _db.GetCollection<Item>(CollectionName)
                                             .Find(_ => true)
                                             .SortByDescending(i => i.Position)
                                             .Limit(1)
                                             .FirstOrDefaultAsync();

        NewItem.Position = (highestPositionItem?.Position ?? 0) + 1;

        // Insert new item
        await _db.GetCollection<Item>(CollectionName).InsertOneAsync(NewItem);
        return RedirectToPage();
    }

    /// <summary>
    /// Handles editing an existing inventory item.
    /// </summary>
    public async Task<IActionResult> OnPostEdit()
    {
        if (string.IsNullOrEmpty(EditItem.Id)) return RedirectToPage();

        var collection = _db.GetCollection<Item>(CollectionName);
        var existingItem = await collection.Find(x => x.Id == EditItem.Id).FirstOrDefaultAsync();

        if (existingItem == null) return NotFound();

        // Preserve the existing Position value
        EditItem.Position = existingItem.Position;

        // Handle File Upload/Preservation
        if (LogoFile != null && LogoFile.Length > 0)
        {
            EditItem.LogoContentType = LogoFile.ContentType;
            using var memoryStream = new MemoryStream();
            await LogoFile.CopyToAsync(memoryStream);
            EditItem.LogoData = memoryStream.ToArray();
        }
        else
        {
            // Retain the existing image data and content type
            EditItem.LogoContentType = existingItem.LogoContentType;
            EditItem.LogoData = existingItem.LogoData;
        }

        await collection.ReplaceOneAsync(x => x.Id == EditItem.Id, EditItem);
        return RedirectToPage();
    }

    /// <summary>
    /// Handles swapping the positions of two selected items.
    /// </summary>
    public async Task<IActionResult> OnPostSwapAsync()
    {
        // 1. Validation: Ensure exactly two distinct IDs were received.
        if (ItemsToSwitch == null || ItemsToSwitch.Length != 2 || string.IsNullOrEmpty(ItemsToSwitch[0]) || string.IsNullOrEmpty(ItemsToSwitch[1]) || ItemsToSwitch[0] == ItemsToSwitch[1])
        {
            return RedirectToPage();
        }

        string id1 = ItemsToSwitch[0];
        string id2 = ItemsToSwitch[1];

        var collection = _db.GetCollection<Item>(CollectionName);

        // 2. Fetch both items from the database
        var itemsToSwap = await collection.Find(
            Builders<Item>.Filter.In(i => i.Id, ItemsToSwitch))
            .ToListAsync();

        if (itemsToSwap.Count != 2)
        {
            return RedirectToPage();
        }

        var item1 = itemsToSwap.First(i => i.Id == id1);
        var item2 = itemsToSwap.First(i => i.Id == id2);

        // 3. Perform the Swap: Swap only their Position values
        (item1.Position, item2.Position) = (item2.Position, item1.Position);

        // 4. Update both documents in the database using BulkWrite
        var updates = new List<WriteModel<Item>>
        {
            new UpdateOneModel<Item>(
                Builders<Item>.Filter.Eq(i => i.Id, item1.Id),
                Builders<Item>.Update.Set(i => i.Position, item1.Position)
            ),
            new UpdateOneModel<Item>(
                Builders<Item>.Filter.Eq(i => i.Id, item2.Id),
                Builders<Item>.Update.Set(i => i.Position, item2.Position)
            )
        };

        await collection.BulkWriteAsync(updates);
        return RedirectToPage();
    }

    /// <summary>
    /// Fixes item ordering by re-assigning sequential position numbers to all items.
    /// </summary>
    public async Task<IActionResult> OnPostReIndex()
    {
        var collection = _db.GetCollection<Item>(CollectionName);

        // 1. Fetch all items, ordered by Position first, then Id for stability.
        var allItems = await collection.Find(_ => true)
            .SortBy(i => i.Position)
            .ThenBy(i => i.Id)
            .ToListAsync();

        var bulkWrites = new List<WriteModel<Item>>();

        // 2. Assign a new, unique, sequential position starting from 1.
        int newPosition = 1;
        foreach (var item in allItems)
        {
            if (item.Position != newPosition)
            {
                bulkWrites.Add(new UpdateOneModel<Item>(
                    Builders<Item>.Filter.Eq(i => i.Id, item.Id),
                    Builders<Item>.Update.Set(i => i.Position, newPosition)
                ));
            }
            newPosition++;
        }

        // 3. Execute bulk update only if changes are needed.
        if (bulkWrites.Count > 0)
        {
            await collection.BulkWriteAsync(bulkWrites);
        }

        return RedirectToPage();
    }


    /// <summary>
    /// Handles deleting multiple selected items at once.
    /// </summary>
    public async Task<IActionResult> OnPostMassDeleteAsync()
    {
        if (string.IsNullOrEmpty(ItemsToDelete)) return RedirectToPage();

        // 1. Parse the comma-separated string of IDs
        var idsToDelete = ItemsToDelete!.Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                                           .Where(id => !string.IsNullOrWhiteSpace(id))
                                           .ToList();

        if (idsToDelete.Count == 0) return RedirectToPage();

        // 2. Create a MongoDB filter to match all IDs in the list
        var filter = Builders<Item>.Filter.In(i => i.Id, idsToDelete);

        // 3. Execute the mass delete operation
        await _db.GetCollection<Item>(CollectionName).DeleteManyAsync(filter);

        return RedirectToPage();
    }


    /// <summary>
    /// Handles deleting a single item (from context menu).
    /// </summary>
    public async Task<IActionResult> OnPostDelete()
    {
        if (string.IsNullOrEmpty(DeleteId)) return RedirectToPage();

        await _db.GetCollection<Item>(CollectionName).DeleteOneAsync(x => x.Id == DeleteId);

        return RedirectToPage();
    }
}

/// <summary>
/// Data Model for an inventory item.
/// </summary>
public class Item
{
    [BsonId]
    [BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)]
    public string? Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal Price { get; set; }

    // Field to define the display order/position
    public int Position { get; set; } = 0;

    // Fields for storing an optional uploaded image
    public byte[]? LogoData { get; set; }
    public string? LogoContentType { get; set; }
}