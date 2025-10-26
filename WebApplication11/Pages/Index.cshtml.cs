using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MongoDB.Driver;
using MongoDB.Bson.Serialization.Attributes;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using System.Linq;

namespace WebApplication11.Pages;

public class IndexModel(IMongoDatabase db) : PageModel
{
    private readonly IMongoDatabase _db = db;
    private const string CollectionName = "Items";

    public List<Item> Items { get; set; } = [];

    // --- Bind Properties for Form Submission ---

    [BindProperty] public Item NewItem { get; set; } = new();

    [BindProperty] public IFormFile? LogoFile { get; set; }

    [BindProperty] public Item EditItem { get; set; } = new();

    [BindProperty] public string? DeleteId { get; set; }

    // Used for the Multi-Select 'Switch' form
    [BindProperty]
    public string[]? ItemsToSwitch { get; set; }

    // NEW: Used for the Multi-Select 'Mass Delete' form
    [BindProperty]
    public string? ItemsToDelete { get; set; }


    // OnGet: Fetches all items, sorted by the new Position field.
    public async Task OnGetAsync()
    {
        // R (Read): Fetches all documents, now sorted by Position.
        Items = await _db.GetCollection<Item>(CollectionName)
                             .Find(_ => true)
                             .SortBy(item => item.Position) // Sort by Position
                             .ToListAsync();
    }

    // OnPostAddAsync: (No change)
    public async Task<IActionResult> OnPostAddAsync()
    {
        // ... (Existing logic for Add)
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

    // OnPostEdit: (No change)
    public async Task<IActionResult> OnPostEdit()
    {
        // ... (Existing logic for Edit)
        if (string.IsNullOrEmpty(EditItem.Id)) return RedirectToPage();

        var collection = _db.GetCollection<Item>(CollectionName);
        var existingItem = await collection.Find(x => x.Id == EditItem.Id).FirstOrDefaultAsync();

        if (existingItem == null) return NotFound();

        // IMPORTANT: Preserve the existing Position value
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
            // FIX: Removed the null-forgiving operator '!' to resolve the warning
            EditItem.LogoData = existingItem.LogoData;
        }

        await collection.ReplaceOneAsync(x => x.Id == EditItem.Id, EditItem);
        return RedirectToPage();
    }

    // OnPostSwapAsync: (No change in logic, still handles the two selected items)
    public async Task<IActionResult> OnPostSwapAsync()
    {
        // 1. Validation: Ensure exactly two distinct IDs were received from the Razor form.
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
        // FIX: Use tuple syntax for cleaner swap (C# 7+)
        (item1.Position, item2.Position) = (item2.Position, item1.Position);

        // 4. Update both documents in the database using BulkWrite (efficient MongoDB update)
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

    // NEW: OnPostMassDeleteAsync: Handles deletion of multiple items.
    public async Task<IActionResult> OnPostMassDeleteAsync()
    {
        if (string.IsNullOrEmpty(ItemsToDelete)) return RedirectToPage();

        // 1. Parse the comma-separated string of IDs into a list of strings
        var idsToDelete = ItemsToDelete.Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                                     .Where(id => !string.IsNullOrWhiteSpace(id))
                                     .ToList();

        if (idsToDelete.Count == 0) return RedirectToPage();

        // 2. Create a MongoDB filter to match all IDs in the list
        var filter = Builders<Item>.Filter.In(i => i.Id, idsToDelete);

        // 3. Execute the mass delete operation
        await _db.GetCollection<Item>(CollectionName).DeleteManyAsync(filter);

        return RedirectToPage();
    }


    // OnPostDelete: (No change in logic, still handles single delete from Edit/Delete modal)
    public async Task<IActionResult> OnPostDelete()
    {
        if (string.IsNullOrEmpty(DeleteId)) return RedirectToPage();

        await _db.GetCollection<Item>(CollectionName).DeleteOneAsync(x => x.Id == DeleteId);

        return RedirectToPage();
    }
}

// Data Model: Item (No change needed here)
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

    public byte[]? LogoData { get; set; }
    public string? LogoContentType { get; set; }
}