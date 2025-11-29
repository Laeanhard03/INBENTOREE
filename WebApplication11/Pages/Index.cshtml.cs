using BCrypt.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
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
    public class MailSettings
    {
        public string SmtpServer { get; set; } = string.Empty;
        public int SmtpPort { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public string SenderEmail { get; set; } = string.Empty;
        public string SmtpUser { get; set; } = string.Empty;
        public string SmtpPass { get; set; } = string.Empty;
    }

    public class User
    {
        [BsonId]
        [BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)]
        public string? Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public bool IsEmailVerified { get; set; } = false;
        public string? EmailVerificationToken { get; set; }
        public DateTime? EmailVerificationTokenExpires { get; set; }
        public string Role { get; set; } = "Seller"; // Default to Seller
    }

    public class LoginInputModel
    {
        [Required]
        [Display(Name = "Username or Email")]
        public string UsernameOrEmail { get; set; } = string.Empty;
        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }

    public class RegisterInputModel
    {
        [Required]
        [StringLength(100, MinimumLength = 3)]
        public string Username { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [DataType(DataType.EmailAddress)]
        [Compare("Email", ErrorMessage = "Emails do not match.")]
        public string ConfirmEmail { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [StringLength(100, MinimumLength = 6)]
        public string Password { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Compare("Password", ErrorMessage = "Passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;

        [Required]
        public string Role { get; set; } = "Seller"; // Added Role field
    }

    public class VerifyCodeInputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [StringLength(6, MinimumLength = 6)]
        public string Code { get; set; } = string.Empty;
    }

    public class IndexModel : PageModel
    {
        private readonly IMongoDatabase _db;
        private const string UserCollectionName = "Users";
        private readonly ILogger<IndexModel> _logger;
        private readonly MailSettings _mailSettings;

        public IndexModel(IMongoDatabase db, ILogger<IndexModel> logger, IOptions<MailSettings> mailSettings)
        {
            _db = db;
            _logger = logger;
            _mailSettings = mailSettings.Value;
        }

        [BindProperty]
        public LoginInputModel LoginInput { get; set; } = new();

        [BindProperty]
        public RegisterInputModel RegisterInput { get; set; } = new();

        public VerifyCodeInputModel VerifyInput { get; set; } = new();

        public IActionResult OnGet()
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                // Check Role claim if available, otherwise default to Dash
                return RedirectToPage("/Dash");
            }
            return Page();
        }

        public async Task<JsonResult> OnPostFirebaseLoginAsync([FromForm] string email, [FromForm] string username)
        {
            if (string.IsNullOrEmpty(email)) return new JsonResult(new { success = false, message = "Email required." });

            var collection = _db.GetCollection<User>(UserCollectionName);
            var user = await collection.Find(u => u.Email == email).FirstOrDefaultAsync();

            if (user == null)
            {
                user = new User
                {
                    Username = string.IsNullOrEmpty(username) ? email.Split('@')[0] : username,
                    Email = email,
                    PasswordHash = "GOOGLE_AUTH_USER",
                    IsEmailVerified = true,
                    Role = "Seller" // Default to Seller for Google Logins for now
                };
                await collection.InsertOneAsync(user);
            }

            await SignInUser(user);
            // Redirect based on role
            string redirect = user.Role == "Customer" ? Url.Page("/Shop") : Url.Page("/Dash");
            return new JsonResult(new { success = true, redirectUrl = redirect });
        }

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
                Response.StatusCode = 401;
                return new JsonResult(new { success = false, message = "Invalid username/email or password." });
            }

            if (!user.IsEmailVerified)
            {
                if (!user.EmailVerificationTokenExpires.HasValue || user.EmailVerificationTokenExpires.Value < DateTime.UtcNow)
                {
                    await SetAndSendVerificationCode(user);
                }

                Response.StatusCode = 401;
                return new JsonResult(new
                {
                    success = false,
                    message = "Your account is not verified. A verification code has been sent to your email.",
                    needsVerification = true,
                    email = user.Email
                });
            }

            await SignInUser(user);

            // Redirect based on Role
            string redirectUrl = (user.Role == "Customer") ? Url.Page("/Shop") : Url.Page("/Dash");

            return new JsonResult(new { success = true, redirectUrl = redirectUrl });
        }

        public async Task<JsonResult> OnPostRegisterAsync([FromForm] RegisterInputModel RegisterInput)
        {
            if (!ModelState.IsValid)
            {
                var errorMsg = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
                Response.StatusCode = 400;
                return new JsonResult(new { success = false, message = errorMsg });
            }

            var collection = _db.GetCollection<User>(UserCollectionName);

            var existingUser = await collection.Find(u => u.Username == RegisterInput.Username).FirstOrDefaultAsync();
            if (existingUser != null) return new JsonResult(new { success = false, message = "Username already taken." });

            var existingEmail = await collection.Find(u => u.Email == RegisterInput.Email).FirstOrDefaultAsync();
            if (existingEmail != null) return new JsonResult(new { success = false, message = "Email already taken." });

            try
            {
                var newUser = new User
                {
                    Username = RegisterInput.Username,
                    Email = RegisterInput.Email,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(RegisterInput.Password),
                    IsEmailVerified = false,
                    Role = RegisterInput.Role // Save the selected role
                };

                await collection.InsertOneAsync(newUser);
                await SetAndSendVerificationCode(newUser);

                return new JsonResult(new
                {
                    success = true,
                    needsVerification = true,
                    email = newUser.Email
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Register Error");
                Response.StatusCode = 500;
                return new JsonResult(new { success = false, message = "Error: " + ex.Message });
            }
        }

        public async Task<JsonResult> OnPostVerifyCodeAsync([FromForm] VerifyCodeInputModel VerifyInput)
        {
            if (!ModelState.IsValid) return new JsonResult(new { success = false, message = "Invalid Data" });

            var collection = _db.GetCollection<User>(UserCollectionName);
            var user = await collection.Find(u => u.Email == VerifyInput.Email).FirstOrDefaultAsync();

            if (user == null) return new JsonResult(new { success = false, message = "User not found." });
            if (user.IsEmailVerified)
            {
                await SignInUser(user);
                string redirect = user.Role == "Customer" ? Url.Page("/Shop") : Url.Page("/Dash");
                return new JsonResult(new { success = true, redirectUrl = redirect });
            }

            if (user.EmailVerificationToken != VerifyInput.Code || user.EmailVerificationTokenExpires < DateTime.UtcNow)
            {
                return new JsonResult(new { success = false, message = "Invalid or expired code." });
            }

            var update = Builders<User>.Update
                .Set(u => u.IsEmailVerified, true)
                .Set(u => u.EmailVerificationToken, null)
                .Set(u => u.EmailVerificationTokenExpires, null);

            await collection.UpdateOneAsync(u => u.Id == user.Id, update);
            await SignInUser(user);

            string redirectUrl = (user.Role == "Customer") ? Url.Page("/Shop") : Url.Page("/Dash");
            return new JsonResult(new { success = true, redirectUrl = redirectUrl });
        }

        public async Task<JsonResult> OnPostResendCodeAsync([FromForm] string email)
        {
            if (string.IsNullOrEmpty(email)) return new JsonResult(new { success = false, message = "Email required" });

            var collection = _db.GetCollection<User>(UserCollectionName);
            var user = await collection.Find(u => u.Email == email).FirstOrDefaultAsync();

            if (user == null || user.IsEmailVerified) return new JsonResult(new { success = false, message = "User invalid." });

            try
            {
                await SetAndSendVerificationCode(user);
                return new JsonResult(new { success = true, message = "New code sent." });
            }
            catch (Exception ex)
            {
                return new JsonResult(new { success = false, message = "Error: " + ex.Message });
            }
        }

        private async Task SetAndSendVerificationCode(User user)
        {
            var collection = _db.GetCollection<User>(UserCollectionName);
            var code = new Random().Next(100000, 999999).ToString();
            var expiry = DateTime.UtcNow.AddMinutes(15);

            var update = Builders<User>.Update
                .Set(u => u.EmailVerificationToken, code)
                .Set(u => u.EmailVerificationTokenExpires, expiry);
            await collection.UpdateOneAsync(u => u.Id == user.Id, update);

            var subject = "Your Verification Code";
            var message = $"<h1>Verification Code</h1><p>Your code is: <strong>{code}</strong></p>";

            await SendEmailAsync(user.Email, subject, message);
        }

        private async Task SignInUser(User user)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.NameIdentifier, user.Id!),
                new(ClaimTypes.Role, user.Role) // Add Role claim
            };

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity),
                new AuthenticationProperties { IsPersistent = true }
            );
        }

        private async Task SendEmailAsync(string toEmail, string subject, string htmlMessage)
        {
            var email = new MimeMessage();
            email.Sender = new MailboxAddress(_mailSettings.SenderName, _mailSettings.SenderEmail);
            email.To.Add(MailboxAddress.Parse(toEmail));
            email.Subject = subject;
            email.Body = new BodyBuilder { HtmlBody = htmlMessage }.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_mailSettings.SmtpServer, _mailSettings.SmtpPort, SecureSocketOptions.StartTls);
            await smtp.AuthenticateAsync(_mailSettings.SmtpUser, _mailSettings.SmtpPass);
            await smtp.SendAsync(email);
            await smtp.DisconnectAsync(true);
        }
    }
}