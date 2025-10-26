using MongoDB.Driver; // Imports the necessary library to communicate with the MongoDB database.

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------
// MongoDB Service Configuration
// ------------------------------------
var client = new MongoClient("mongodb://localhost:27017"); // Establishes the database client using the default local MongoDB server address.

// FIX: Standardized the database name to all lowercase to avoid file-system casing errors (MongoWriteException) that can occur in some operating environments.
var db = client.GetDatabase("inventorydb");

// Registers the MongoDB database object for Dependency Injection (DI). This allows other parts of the application (like the IndexModel) to easily request and use the database connection.
builder.Services.AddSingleton(db);
builder.Services.AddRazorPages(); // Enables the application to recognize and process Razor Pages (.cshtml and .cshtml.cs files).

// ------------------------------------
// Application Build and Run
// ------------------------------------
var app = builder.Build();

app.UseStaticFiles(); // Enables the web server to correctly serve static content like CSS, images, and JavaScript files from the wwwroot folder.
app.MapRazorPages(); // Maps incoming URL requests to the correct Razor Page file.
app.Run(); // Starts the web application, making it accessible via a web browser.
