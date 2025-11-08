using BCrypt.Net;
using MailKit.Net.Smtp; // ADDED: For MailKit
using MailKit.Security; // ADDED: For MailKit
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options; // ADDED: For MailSettings
using MimeKit; // ADDED: For MailKit
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace WebApplication11.Pages
{
    // --- NEW: MailSettings class defined here --- 
    // This class maps to the "MailSettings" in appsettings.json
    public class MailSettings
    {
        public string SmtpServer { get; set; } = string.Empty;
        public int SmtpPort { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public string SenderEmail { get; set; } = string.Empty;
        public string SmtpUser { get; set; } = string.Empty;
        public string SmtpPass { get; set; } = string.Empty;
    }

    /// <summary>
    /// Data Model for a User in the "Users" collection.
    /// </summary>
    public class User
    {
        [BsonId]
        [BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)]
        public string? Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;

        // --- NEW VERIFICATION FIELDS ---
        public bool IsEmailVerified { get; set; } = false;
        public string? EmailVerificationToken { get; set; }
        public DateTime? EmailVerificationTokenExpires { get; set; }
    }

    /// <summary>
    /// Input Model for the Login form.
    /// </summary>
    public class LoginInputModel
    {
        [Required]
        [Display(Name = "Username or Email")]
        public string UsernameOrEmail { get; set; } = string.Empty;
        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }

    /// <summary>
    /// Input Model for the Registration form.
    /// </summary>
    public class RegisterInputModel
    {
        [Required]
        [StringLength(100, ErrorMessage = "The {0} must be at least {2} characters long.", MinimumLength = 3)]
        public string Username { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        [Display(Name = "Email Address")]
        public string Email { get; set; } = string.Empty;

        [DataType(DataType.EmailAddress)]
        [Display(Name = "Confirm Email")]
        [Compare("Email", ErrorMessage = "The email and confirmation email do not match.")]
        public string ConfirmEmail { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [StringLength(100, ErrorMessage = "The {0} must be at least {2} characters long.", MinimumLength = 6)]
        public string Password { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Display(Name = "Confirm Password")]
        [Compare("Password", ErrorMessage = "The password and confirmation password do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    // --- NEW INPUT MODEL FOR VERIFICATION ---
    public class VerifyCodeInputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [StringLength(6, MinimumLength = 6, ErrorMessage = "The code must be 6 digits.")]
        [Display(Name = "Verification Code")]
        public string Code { get; set; } = string.Empty;
    }

    /// <summary>
    /// PageModel for the Index page (Login/Register/Verify).
    /// </summary>
    public class IndexModel : PageModel
    {
        private readonly IMongoDatabase _db;
        private const string UserCollectionName = "Users";
        private readonly ILogger<IndexModel> _logger;
        private readonly MailSettings _mailSettings; // ADDED: For storing mail settings

        // Constructor updated to receive database, logger, and NEW mail settings
        public IndexModel(IMongoDatabase db, ILogger<IndexModel> logger, IOptions<MailSettings> mailSettings)
        {
            _db = db;
            _logger = logger;
            _mailSettings = mailSettings.Value; // ADDED: Get settings from IOptions
        }

        // These properties are NOT bound, which is correct for this page.
        // The data will be passed directly to the handlers via [FromForm]
        public LoginInputModel LoginInput { get; set; } = new();
        public RegisterInputModel RegisterInput { get; set; } = new();

        // --- THE FIX ---
        // Removed [BindProperty] from here.
        // This stops it from being validated on every POST, fixing the 400 error.
        public VerifyCodeInputModel VerifyInput { get; set; } = new();
        // --- END THE FIX ---


        public IActionResult OnGet()
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                _logger.LogInformation("User {Username} already authenticated, redirecting to Dashboard.", User.Identity.Name);
                return RedirectToPage("/Dash");
            }
            return Page();
        }

        /// <summary>
        /// Handles the Login form submission via AJAX.
        /// </summary>
        public async Task<JsonResult> OnPostLoginAsync([FromForm] LoginInputModel LoginInput)
        {
            if (!ModelState.IsValid)
            {
                Response.StatusCode = 400;
                return new JsonResult(new { success = false, message = "Invalid data submitted." });
            }

            var collection = _db.GetCollection<User>(UserCollectionName);

            var loginInput = LoginInput.UsernameOrEmail;
            var user = await collection.Find(u => u.Username == loginInput || u.Email == loginInput).FirstOrDefaultAsync();

            if (user == null || !BCrypt.Net.BCrypt.Verify(LoginInput.Password, user.PasswordHash))
            {
                _logger.LogWarning("Login failed for user {UsernameOrEmail}: Invalid credentials.", LoginInput.UsernameOrEmail);
                Response.StatusCode = 401; // Unauthorized
                return new JsonResult(new { success = false, message = "Invalid username/email or password." });
            }

            // --- NEW VERIFICATION CHECK ---
            if (!user.IsEmailVerified)
            {
                _logger.LogWarning("Login failed for user {Username}: Email not verified.", user.Username);

                // Re-send code if the old one is expired or missing
                if (!user.EmailVerificationTokenExpires.HasValue || user.EmailVerificationTokenExpires.Value < DateTime.UtcNow)
                {
                    await SetAndSendVerificationCode(user);
                }

                Response.StatusCode = 401; // Unauthorized
                return new JsonResult(new
                {
                    success = false,
                    message = "Your account is not verified. A verification code has been sent to your email.",
                    needsVerification = true, // Flag for the frontend
                    email = user.Email
                });
            }
            // --- END VERIFICATION CHECK ---


            // --- User is valid AND verified, sign them in ---
            _logger.LogInformation("User {Username} logged in successfully.", user.Username);
            await SignInUser(user);

            return new JsonResult(new { success = true, redirectUrl = Url.Page("/Dash") });
        }

        /// <summary>
        /// Handles the Registration form submission via AJAX.
        /// </summary>
        public async Task<JsonResult> OnPostRegisterAsync([FromForm] RegisterInputModel RegisterInput)
        {
            // This is the model that will be validated.
            // Because VerifyInput is no longer a [BindProperty],
            // only the RegisterInput model's state will be checked.
            if (!ModelState.IsValid)
            {
                var errorMsg = string.Join(" ", ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage));

                Response.StatusCode = 400; // Bad Request
                return new JsonResult(new { success = false, message = $"Registration failed: {errorMsg}" });
            }

            _logger.LogInformation("Attempting to register new user: {Username}", RegisterInput.Username);

            var collection = _db.GetCollection<User>(UserCollectionName);

            var existingUser = await collection.Find(u => u.Username == RegisterInput.Username).FirstOrDefaultAsync();
            if (existingUser != null)
            {
                Response.StatusCode = 409; // Conflict
                _logger.LogWarning("Registration failed: Username {Username} already taken.", RegisterInput.Username);
                return new JsonResult(new { success = false, message = "Registration failed. Username is already taken." });
            }

            var existingEmail = await collection.Find(u => u.Email == RegisterInput.Email).FirstOrDefaultAsync();
            if (existingEmail != null)
            {
                Response.StatusCode = 409; // Conflict
                _logger.LogWarning("Registration failed: Email {Email} already taken.", RegisterInput.Email);
                return new JsonResult(new { success = false, message = "Registration failed. Email is already taken." });
            }

            try
            {
                var passwordHash = BCrypt.Net.BCrypt.HashPassword(RegisterInput.Password);

                var newUser = new User
                {
                    Username = RegisterInput.Username,
                    Email = RegisterInput.Email,
                    PasswordHash = passwordHash,
                    IsEmailVerified = false // Set verification to false
                };

                // --- MODIFIED: Don't log in. Save user, send code. ---
                await collection.InsertOneAsync(newUser);
                _logger.LogInformation("Successfully inserted new user {Username} to MongoDB.", newUser.Username);

                await SetAndSendVerificationCode(newUser);

                // Return a special success message telling the frontend to switch views
                return new JsonResult(new
                {
                    success = true,
                    needsVerification = true, // Flag for the frontend
                    email = newUser.Email
                });
            }
            catch (Exception ex)
            {
                // This will now catch errors from SendEmailAsync,
                // such as "5.7.0 Authentication Required" if your credentials are bad
                _logger.LogError(ex, "Database or Email error while registering user {Username}.", RegisterInput.Username);
                Response.StatusCode = 500; // Internal Server Error

                // Send a more detailed error to the client for debugging email issues
                string errorDetail = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                return new JsonResult(new { success = false, message = $"An error occurred during registration: {errorDetail}" });
            }
        }

        // --- NEW HANDLER: OnPostVerifyCodeAsync ---
        /// <summary>
        /// Handles the 6-digit code verification via AJAX.
        /// </summary>
        public async Task<JsonResult> OnPostVerifyCodeAsync([FromForm] VerifyCodeInputModel VerifyInput)
        {
            if (!ModelState.IsValid)
            {
                var errorMsg = string.Join(" ", ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage));

                Response.StatusCode = 400;
                return new JsonResult(new { success = false, message = $"Invalid data: {errorMsg}" });
            }

            var collection = _db.GetCollection<User>(UserCollectionName);
            var user = await collection.Find(u => u.Email == VerifyInput.Email).FirstOrDefaultAsync();

            if (user == null)
            {
                Response.StatusCode = 404;
                return new JsonResult(new { success = false, message = "User not found." });
            }

            if (user.IsEmailVerified)
            {
                await SignInUser(user); // Log them in if they were just checking
                return new JsonResult(new { success = true, redirectUrl = Url.Page("/Dash") }); // Already verified
            }

            if (user.EmailVerificationToken != VerifyInput.Code || !user.EmailVerificationTokenExpires.HasValue || user.EmailVerificationTokenExpires.Value < DateTime.UtcNow)
            {
                Response.StatusCode = 400;
                return new JsonResult(new { success = false, message = "Invalid or expired verification code." });
            }

            // --- Success! Verify the user ---
            var update = Builders<User>.Update
                .Set(u => u.IsEmailVerified, true)
                .Set(u => u.EmailVerificationToken, null) // Clear the token
                .Set(u => u.EmailVerificationTokenExpires, null);

            await collection.UpdateOneAsync(u => u.Id == user.Id, update);

            _logger.LogInformation("User {Username} verified their email successfully.", user.Username);

            // Automatically sign them in
            await SignInUser(user);

            return new JsonResult(new { success = true, redirectUrl = Url.Page("/Dash") });
        }

        // --- NEW HANDLER: OnPostResendCodeAsync ---
        /// <summary>
        /// Handles resending the verification code via AJAX.
        /// </summary>
        public async Task<JsonResult> OnPostResendCodeAsync([FromForm] string email)
        {
            if (string.IsNullOrEmpty(email))
            {
                Response.StatusCode = 400;
                return new JsonResult(new { success = false, message = "Email is required." });
            }

            var collection = _db.GetCollection<User>(UserCollectionName);
            var user = await collection.Find(u => u.Email == email).FirstOrDefaultAsync();

            if (user == null || user.IsEmailVerified)
            {
                Response.StatusCode = 404;
                return new JsonResult(new { success = false, message = "User not found or is already verified." });
            }

            try
            {
                await SetAndSendVerificationCode(user);
                return new JsonResult(new { success = true, message = "A new verification code has been sent." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resend code for {Email}", email);
                Response.StatusCode = 500;
                string errorDetail = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                return new JsonResult(new { success = false, message = $"Failed to send email: {errorDetail}" });
            }
        }


        // --- NEW HELPER METHODS ---

        /// <summary>
        /// Generates, saves, and emails a new verification code for a user.
        /// </summary>
        private async Task SetAndSendVerificationCode(User user)
        {
            var collection = _db.GetCollection<User>(UserCollectionName);
            var code = new Random().Next(100000, 999999).ToString(); // 6-digit code
            var expiry = DateTime.UtcNow.AddMinutes(15);

            // Update user in DB
            var update = Builders<User>.Update
                .Set(u => u.EmailVerificationToken, code)
                .Set(u => u.EmailVerificationTokenExpires, expiry);
            await collection.UpdateOneAsync(u => u.Id == user.Id, update);

            // Send email
            var subject = "Your Verification Code";
            var message = $"<h1>Welcome to Inventory System!</h1>"
                          + $"<p>Your verification code is: <strong>{code}</strong></p>"
                          + $"<p>This code will expire in 15 minutes.</p>";

            await SendEmailAsync(user.Email, subject, message);
            _logger.LogInformation("Sent verification code to {Email}", user.Email);
        }

        /// <summary>
        /// Signs in a user by creating an auth cookie.
        /// </summary>
        private async Task SignInUser(User user)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.NameIdentifier, user.Id!)
            };

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var authProperties = new AuthenticationProperties
            {
                IsPersistent = true // Remember the user
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity),
                authProperties);
        }

        /// <summary>
        /// NEW: Email sending logic using MailKit, contained within this file.
        /// </summary>
        private async Task SendEmailAsync(string toEmail, string subject, string htmlMessage)
        {
            var email = new MimeMessage
            {
                Sender = new MailboxAddress(_mailSettings.SenderName, _mailSettings.SenderEmail)
            };
            email.To.Add(MailboxAddress.Parse(toEmail));
            email.Subject = subject;

            var builder = new BodyBuilder
            {
                HtmlBody = htmlMessage
            };
            email.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_mailSettings.SmtpServer, _mailSettings.SmtpPort, SecureSocketOptions.StartTls);
            await smtp.AuthenticateAsync(_mailSettings.SmtpUser, _mailSettings.SmtpPass);
            await smtp.SendAsync(email);
            await smtp.DisconnectAsync(true);
        }
    }
}
