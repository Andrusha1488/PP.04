using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using StudentDiaryWeb.Data;
using StudentDiaryWeb.Models;
using StudentDiaryWeb.Services;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace StudentDiaryWeb.Controllers
{
    [Authorize]
    public class ProfileController : Controller
    {
        private int CurrentUserID => int.Parse(
            User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
        private string CurrentLogin => User.Identity?.Name ?? "";
        private string CurrentRole => User.FindFirst(ClaimTypes.Role)?.Value ?? "";
        private string CurrentIP =>
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

        // ── ПРОФИЛЬ ────────────────────────────────────────────────────────

        public IActionResult Index()
        {
            var model = new ProfileViewModel
            {
                Login = CurrentLogin,
                FullName = User.FindFirst("FullName")?.Value ?? "",
                Role = CurrentRole,
                Email = GetUserEmail()
            };
            return View(model);
        }

        // ── ИЗМЕНЕНИЕ EMAIL ────────────────────────────────────────────────

        [HttpGet]
        public IActionResult EditEmail()
        {
            ViewBag.CurrentEmail = GetUserEmail();
            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult EditEmail(string Email)
        {
            if (string.IsNullOrWhiteSpace(Email) || !Email.Contains('@'))
            {
                TempData["Error"] = "Введите корректный email адрес.";
                return RedirectToAction("EditEmail");
            }

            try
            {
                using var conn = DatabaseHelper.GetConnection();

                // Email хранится в таблице Users — единый источник для всех ролей
                using var cmd = new SqlCommand(
                    "UPDATE Users SET Email=@E WHERE UserID=@ID", conn);
                cmd.Parameters.AddWithValue("@E", Email.Trim());
                cmd.Parameters.AddWithValue("@ID", CurrentUserID);
                cmd.ExecuteNonQuery();

                // Дублируем в профильную таблицу для удобства запросов
                string? profileQuery = CurrentRole switch
                {
                    "Преподаватель" => "UPDATE Teachers  SET Email=@E WHERE UserID=@ID",
                    "Методист" => "UPDATE Methodists SET Email=@E WHERE UserID=@ID",
                    _ => null
                };
                if (profileQuery != null)
                {
                    using var cmd2 = new SqlCommand(profileQuery, conn);
                    cmd2.Parameters.AddWithValue("@E", Email.Trim());
                    cmd2.Parameters.AddWithValue("@ID", CurrentUserID);
                    cmd2.ExecuteNonQuery();
                }

                AuditService.Log(CurrentUserID, CurrentLogin,
                    "Изменение email", "Users",
                    $"Email изменён на {Email}", CurrentIP);

                TempData["Success"] = "Email успешно обновлён!";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Index");
        }

        // ── СМЕНА ПАРОЛЯ ───────────────────────────────────────────────────

        [HttpGet]
        public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult ChangePassword(ChangePasswordViewModel model)
        {
            if (model.NewPassword != model.ConfirmPassword)
            { model.ErrorMessage = "Пароли не совпадают."; return View(model); }

            if (model.NewPassword.Length < 6)
            { model.ErrorMessage = "Минимум 6 символов."; return View(model); }

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                string oldHash = HashPassword(model.OldPassword);

                using var check = new SqlCommand(
                    "SELECT COUNT(*) FROM Users WHERE UserID=@ID AND PasswordHash=@H",
                    conn);
                check.Parameters.AddWithValue("@ID", CurrentUserID);
                check.Parameters.AddWithValue("@H", oldHash);

                if ((int)check.ExecuteScalar() == 0)
                { model.ErrorMessage = "Текущий пароль неверен."; return View(model); }

                string newHash = HashPassword(model.NewPassword);
                using var upd = new SqlCommand(
                    "UPDATE Users SET PasswordHash=@H WHERE UserID=@ID", conn);
                upd.Parameters.AddWithValue("@H", newHash);
                upd.Parameters.AddWithValue("@ID", CurrentUserID);
                upd.ExecuteNonQuery();

                AuditService.Log(CurrentUserID, CurrentLogin,
                    "Смена пароля", "Users", "Пароль изменён", CurrentIP);

                model.SuccessMessage = "Пароль успешно изменён!";
            }
            catch (Exception ex) { model.ErrorMessage = ex.Message; }
            return View(model);
        }

        // ── ВСПОМОГАТЕЛЬНЫЕ ───────────────────────────────────────────────

        private string GetUserEmail()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(
                    "SELECT ISNULL(Email,'') FROM Users WHERE UserID=@ID", conn);
                cmd.Parameters.AddWithValue("@ID", CurrentUserID);
                return cmd.ExecuteScalar()?.ToString() ?? "";
            }
            catch { return ""; }
        }

        private static string HashPassword(string p)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(
                sha.ComputeHash(Encoding.UTF8.GetBytes(p))).ToLower();
        }
    }

    // Вьюмодель профиля — здесь, рядом с контроллером
    public class ProfileViewModel
    {
        public string Login { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }
}