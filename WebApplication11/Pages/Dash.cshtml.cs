using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MongoDB.Driver;
using MongoDB.Bson.Serialization.Attributes;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;

// --- PDF Generation Imports ---
using iText.Kernel.Pdf;
using iText.Layout.Properties;
using iText.Layout.Element;
using Document = iText.Layout.Document; // Fixes 'Document' ambiguity
using Table = iText.Layout.Element.Table; // Fixes 'Table' ambiguity
using iText.IO.Font.Constants; // For StandardFonts
using iText.Kernel.Font;       // For PdfFontFactory
// -----------------------------

namespace WebApplication11.Pages;

[Authorize]
public class DashModel(IMongoDatabase db, ILogger<DashModel> logger) : PageModel
{
    private readonly IMongoDatabase _db = db;
    private readonly ILogger<DashModel> _logger = logger;
    private const string CollectionName = "Items";

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

    public async Task OnGetAsync()
    {
        _logger.LogInformation("User {Username} successfully accessed the dashboard.", User.Identity?.Name ?? "Unknown");

        Items = await _db.GetCollection<Item>(CollectionName)
                             .Find(_ => true)
                             .SortBy(item => item.Position)
                             .ToListAsync();
    }

    /// <summary>
    /// Generates and downloads a PDF report of all inventory items.
    /// </summary>
    public async Task<IActionResult> OnGetPdfReportAsync()
    {
        // 1. Fetch items 
        var items = await _db.GetCollection<Item>(CollectionName)
                             .Find(_ => true)
                             .SortBy(i => i.Position)
                             .ToListAsync();

        using var stream = new MemoryStream();
        using var writer = new PdfWriter(stream);
        using var pdf = new PdfDocument(writer);
        using var document = new Document(pdf);

        // Create a Bold Font to use instead of .SetBold()
        PdfFont boldFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);

        // 2. Add Header
        document.Add(new Paragraph(new Text("Inventory Report").SetFont(boldFont).SetFontSize(20))
            .SetTextAlignment(TextAlignment.CENTER));

        document.Add(new Paragraph($"Generated on: {DateTime.Now:yyyy-MM-dd HH:mm}")
            .SetTextAlignment(TextAlignment.CENTER)
            .SetFontSize(10));

        document.Add(new Paragraph("\n")); // Spacer

        // 3. Create Table (3 columns: Name, Quantity, Price)
        var table = new Table(new float[] { 4, 2, 2 }).UseAllAvailableWidth();

        // Table Headers
        table.AddHeaderCell(new Paragraph(new Text("Item Name").SetFont(boldFont)));
        table.AddHeaderCell(new Paragraph(new Text("Quantity").SetFont(boldFont)).SetTextAlignment(TextAlignment.RIGHT));
        table.AddHeaderCell(new Paragraph(new Text("Price").SetFont(boldFont)).SetTextAlignment(TextAlignment.RIGHT));

        // Table Rows
        foreach (var item in items)
        {
            table.AddCell(new Paragraph(item.Name));
            table.AddCell(new Paragraph(item.Quantity.ToString()).SetTextAlignment(TextAlignment.RIGHT));
            table.AddCell(new Paragraph(item.Price.ToString("C")).SetTextAlignment(TextAlignment.RIGHT));
        }

        document.Add(table);
        document.Close();

        // 4. Return the file
        return File(stream.ToArray(), "application/pdf", $"InventoryReport_{DateTime.Now:yyyyMMdd}.pdf");
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        _logger.LogInformation("User {Username} is logging out.", User.Identity?.Name ?? "Unknown");
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        TempData["success"] = "You have been logged out.";
        return RedirectToPage("/Index");
    }

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

        var highestPositionItem = await _db.GetCollection<Item>(CollectionName)
                                             .Find(_ => true)
                                             .SortByDescending(i => i.Position)
                                             .Limit(1)
                                             .FirstOrDefaultAsync();

        NewItem.Position = (highestPositionItem?.Position ?? 0) + 1;

        await _db.GetCollection<Item>(CollectionName).InsertOneAsync(NewItem);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEdit()
    {
        if (string.IsNullOrEmpty(EditItem.Id)) return RedirectToPage();

        var collection = _db.GetCollection<Item>(CollectionName);
        var existingItem = await collection.Find(x => x.Id == EditItem.Id).FirstOrDefaultAsync();

        if (existingItem == null) return NotFound();

        EditItem.Position = existingItem.Position;

        if (LogoFile != null && LogoFile.Length > 0)
        {
            EditItem.LogoContentType = LogoFile.ContentType;
            using var memoryStream = new MemoryStream();
            await LogoFile.CopyToAsync(memoryStream);
            EditItem.LogoData = memoryStream.ToArray();
        }
        else
        {
            EditItem.LogoContentType = existingItem.LogoContentType;
            EditItem.LogoData = existingItem.LogoData;
        }

        await collection.ReplaceOneAsync(x => x.Id == EditItem.Id, EditItem);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSwapAsync()
    {
        if (ItemsToSwitch == null || ItemsToSwitch.Length != 2 || string.IsNullOrEmpty(ItemsToSwitch[0]) || string.IsNullOrEmpty(ItemsToSwitch[1]) || ItemsToSwitch[0] == ItemsToSwitch[1])
        {
            return RedirectToPage();
        }

        string id1 = ItemsToSwitch[0];
        string id2 = ItemsToSwitch[1];

        var collection = _db.GetCollection<Item>(CollectionName);

        var itemsToSwap = await collection.Find(
            Builders<Item>.Filter.In(i => i.Id, ItemsToSwitch))
            .ToListAsync();

        if (itemsToSwap.Count != 2)
        {
            return RedirectToPage();
        }

        var item1 = itemsToSwap.First(i => i.Id == id1);
        var item2 = itemsToSwap.First(i => i.Id == id2);

        (item1.Position, item2.Position) = (item2.Position, item1.Position);

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

    public async Task<IActionResult> OnPostReIndex()
    {
        var collection = _db.GetCollection<Item>(CollectionName);

        var allItems = await collection.Find(_ => true)
            .SortBy(i => i.Position)
            .ThenBy(i => i.Id)
            .ToListAsync();

        var bulkWrites = new List<WriteModel<Item>>();

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

        if (bulkWrites.Count > 0)
        {
            await collection.BulkWriteAsync(bulkWrites);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostMassDeleteAsync()
    {
        if (string.IsNullOrEmpty(ItemsToDelete)) return RedirectToPage();

        var idsToDelete = ItemsToDelete!.Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                                           .Where(id => !string.IsNullOrWhiteSpace(id))
                                           .ToList();

        if (idsToDelete.Count == 0) return RedirectToPage();

        var filter = Builders<Item>.Filter.In(i => i.Id, idsToDelete);

        await _db.GetCollection<Item>(CollectionName).DeleteManyAsync(filter);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDelete()
    {
        if (string.IsNullOrEmpty(DeleteId)) return RedirectToPage();

        await _db.GetCollection<Item>(CollectionName).DeleteOneAsync(x => x.Id == DeleteId);

        return RedirectToPage();
    }
}

public class Item
{
    [BsonId]
    [BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)]
    public string? Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public int Position { get; set; } = 0;
    public byte[]? LogoData { get; set; }
    public string? LogoContentType { get; set; }
}