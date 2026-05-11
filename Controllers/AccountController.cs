using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using StudentDiaryWeb.Data;
using StudentDiaryWeb.Models;
using StudentDiaryWeb.Services;

namespace StudentDiaryWeb.Controllers
{
    public class AccountController : Controller
    {
        private readonly LoginProtectionService _protection;
        private readonly TwoFactorService _twoFactor;
        private readonly EmailService _email;

        public AccountController(LoginProtectionService protection,
                                  TwoFactorService twoFactor,
                                  EmailService email)
        {
            _protection = protection;
            _twoFactor = twoFactor;
            _email = email;
        }

        // ── ВХОД ──────────────────────────────────────────────────────────

        [HttpGet]
        public IActionResult Login()
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToAction("Index", "Home");

            if (TempData["RegisterSuccess"] != null)
                ViewBag.RegisterSuccess = TempData["RegisterSuccess"];

            return View(new LoginViewModel());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel model)
        {
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (_protection.IsBlocked(ip))
            {
                int minutes = _protection.GetRemainingBlockMinutes(ip);
                model.ErrorMessage = $"Слишком много попыток. Попробуйте через {minutes} мин.";
                return View(model);
            }

            if (string.IsNullOrWhiteSpace(model.Login) ||
                string.IsNullOrWhiteSpace(model.Password))
            {
                model.ErrorMessage = "Введите логин и пароль.";
                return View(model);
            }

            string hash = HashPassword(model.Password);

            try
            {
                using var conn = DatabaseHelper.GetConnection();

                using var cmd = new SqlCommand(@"
                    SELECT u.UserID, u.Login, r.RoleName, u.RoleID
                    FROM Users u
                    JOIN Roles r ON u.RoleID = r.RoleID
                    WHERE u.Login = @Login
                      AND u.PasswordHash = @Hash
                      AND u.IsActive = 1", conn);
                cmd.Parameters.AddWithValue("@Login", model.Login.Trim());
                cmd.Parameters.AddWithValue("@Hash", hash);
                using var reader = cmd.ExecuteReader();

                if (!reader.Read())
                {
                    _protection.RegisterFailedAttempt(ip);
                    AuditService.Log(0, model.Login, "Неудача входа", "Users",
                        "Неудачная попытка входа", ip);
                    model.ErrorMessage = "Неверный логин или пароль.";
                    return View(model);
                }

                int userID = (int)reader["UserID"];
                string login = reader["Login"].ToString()!;
                string role = reader["RoleName"].ToString()!;
                int roleID = (int)reader["RoleID"];
                reader.Close();

                _protection.ResetAttempts(ip);

                // Получаем email — если он есть, отправляем 2FA
                string? userEmail = GetUserEmail(conn, userID);

                if (string.IsNullOrEmpty(userEmail))
                {
                    // Нет email — входим без 2FA
                    string fullName = LoadFullName(conn, userID, role, out int profileID);
                    await SignInUser(userID, login, role, fullName, profileID);
                    AuditService.Log(userID, login, "Вход (без 2FA)", "Users",
                        "Вход без 2FA — email не указан", ip);
                    return RedirectToAction("Index", "Home");
                }

                // Генерируем и отправляем код
                string code = _twoFactor.GenerateCode(login);

                string emailBody = $@"
                    <div style='font-family:Arial,sans-serif;max-width:480px;margin:0 auto;'>
                        <h2 style='color:#1e40af;'>Электронный журнал преподавателя</h2>
                        <p>Для входа в систему введите код подтверждения:</p>
                        <div style='font-size:36px;font-weight:bold;
                                    letter-spacing:8px;color:#111827;
                                    background:#f3f4f6;padding:20px;
                                    border-radius:8px;text-align:center;
                                    margin:20px 0;'>
                            {code}
                        </div>
                        <p style='color:#6b7280;font-size:13px;'>
                            Код действителен 5 минут.<br/>
                            Если вы не запрашивали вход — проигнорируйте это письмо.
                        </p>
                    </div>";

                await _email.SendAsync(userEmail,
                    "Код подтверждения — Электронный журнал", emailBody);

                HttpContext.Session.SetString("2fa_login", login);
                HttpContext.Session.SetString("2fa_role", role);
                HttpContext.Session.SetInt32("2fa_userid", userID);
                HttpContext.Session.SetInt32("2fa_roleid", roleID);
                HttpContext.Session.SetString("2fa_email", userEmail);
                HttpContext.Session.SetString("2fa_ip", ip);

                AuditService.Log(userID, login, "2FA отправлен", "Users",
                    $"Код 2FA отправлен на {MaskEmail(userEmail)}", ip);

                return RedirectToAction("Verify2FA");
            }
            catch (Exception ex)
            {
                model.ErrorMessage = $"Ошибка: {ex.Message}";
                return View(model);
            }
        }

        // ── ПОДТВЕРЖДЕНИЕ 2FA ─────────────────────────────────────────────

        [HttpGet]
        public IActionResult Verify2FA()
        {
            string? login = HttpContext.Session.GetString("2fa_login");
            if (string.IsNullOrEmpty(login))
                return RedirectToAction("Login");

            string email = HttpContext.Session.GetString("2fa_email") ?? "";
            ViewBag.MaskedEmail = MaskEmail(email);
            ViewBag.RemainingSeconds = _twoFactor.GetRemainingSeconds(login);
            return View(new TwoFactorViewModel { Login = login });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Verify2FA(TwoFactorViewModel model)
        {
            string? login = HttpContext.Session.GetString("2fa_login");
            string? role = HttpContext.Session.GetString("2fa_role");
            int userID = HttpContext.Session.GetInt32("2fa_userid") ?? 0;
            string ip = HttpContext.Session.GetString("2fa_ip") ?? "";
            string email = HttpContext.Session.GetString("2fa_email") ?? "";

            if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(role))
                return RedirectToAction("Login");

            if (string.IsNullOrWhiteSpace(model.Code))
            {
                model.ErrorMessage = "Введите код подтверждения.";
                ViewBag.MaskedEmail = MaskEmail(email);
                ViewBag.RemainingSeconds = _twoFactor.GetRemainingSeconds(login);
                return View(model);
            }

            if (!_twoFactor.VerifyCode(login, model.Code))
            {
                model.ErrorMessage = "Неверный или устаревший код. Попробуйте войти снова.";
                ViewBag.MaskedEmail = MaskEmail(email);
                ViewBag.RemainingSeconds = _twoFactor.GetRemainingSeconds(login);
                return View(model);
            }

            using var conn = DatabaseHelper.GetConnection();
            string fullName = LoadFullName(conn, userID, role, out int profileID);
            await SignInUser(userID, login, role, fullName, profileID);

            HttpContext.Session.Remove("2fa_login");
            HttpContext.Session.Remove("2fa_role");
            HttpContext.Session.Remove("2fa_userid");
            HttpContext.Session.Remove("2fa_roleid");
            HttpContext.Session.Remove("2fa_email");
            HttpContext.Session.Remove("2fa_ip");

            AuditService.Log(userID, login, "Вход", "Users", "Успешный вход с 2FA", ip);
            return RedirectToAction("Index", "Home");
        }

        // ── ПОВТОРНАЯ ОТПРАВКА КОДА ────────────────────────────────────────

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ResendCode()
        {
            string? login = HttpContext.Session.GetString("2fa_login");
            string? email = HttpContext.Session.GetString("2fa_email");

            if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(email))
                return RedirectToAction("Login");

            string code = _twoFactor.GenerateCode(login);

            string emailBody = $@"
                <div style='font-family:Arial,sans-serif;max-width:480px;margin:0 auto;'>
                    <h2 style='color:#1e40af;'>Электронный журнал преподавателя</h2>
                    <p>Ваш новый код подтверждения:</p>
                    <div style='font-size:36px;font-weight:bold;
                                letter-spacing:8px;color:#111827;
                                background:#f3f4f6;padding:20px;
                                border-radius:8px;text-align:center;
                                margin:20px 0;'>
                        {code}
                    </div>
                    <p style='color:#6b7280;font-size:13px;'>Код действителен 5 минут.</p>
                </div>";

            await _email.SendAsync(email,
                "Новый код подтверждения — Электронный журнал", emailBody);

            TempData["Info"] = "Новый код отправлен на вашу почту.";
            return RedirectToAction("Verify2FA");
        }

        // ── ВЫХОД ──────────────────────────────────────────────────────────

        public async Task<IActionResult> Logout()
        {
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
            string login = User.Identity?.Name ?? "";
            int userID = int.TryParse(
                User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int id) ? id : 0;

            AuditService.Log(userID, login, "Выход", "Users", "Выход из системы", ip);
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Login");
        }

        public IActionResult AccessDenied() => View();

        // ── РЕГИСТРАЦИЯ ────────────────────────────────────────────────────
        // Используется только администратором через /Admin/Users
        // Прямая регистрация через форму — только если нужна

        [HttpGet]
        public IActionResult Register()
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToAction("Index", "Home");
            ViewBag.Roles = GetRoles();
            return View(new RegisterViewModel());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Register(RegisterViewModel model)
        {
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (string.IsNullOrWhiteSpace(model.Login) || model.Login.Length < 3)
                model.ErrorMessage = "Логин должен содержать не менее 3 символов.";
            else if (string.IsNullOrWhiteSpace(model.Password) || model.Password.Length < 6)
                model.ErrorMessage = "Пароль должен содержать не менее 6 символов.";
            else if (model.Password != model.ConfirmPassword)
                model.ErrorMessage = "Пароли не совпадают.";
            else if (string.IsNullOrWhiteSpace(model.LastName) ||
                     string.IsNullOrWhiteSpace(model.FirstName))
                model.ErrorMessage = "Введите фамилию и имя.";

            if (!string.IsNullOrEmpty(model.ErrorMessage))
            {
                ViewBag.Roles = GetRoles();
                return View(model);
            }

            try
            {
                using var conn = DatabaseHelper.GetConnection();

                using var chk = new SqlCommand(
                    "SELECT COUNT(*) FROM Users WHERE Login=@L", conn);
                chk.Parameters.AddWithValue("@L", model.Login.Trim());
                if ((int)chk.ExecuteScalar() > 0)
                {
                    model.ErrorMessage = "Этот логин уже занят.";
                    ViewBag.Roles = GetRoles();
                    return View(model);
                }

                string hash = HashPassword(model.Password);

                using var ins = new SqlCommand(@"
                    INSERT INTO Users (Login, PasswordHash, RoleID, IsActive, Email)
                    VALUES (@L, @H, @R, 1, @E);
                    SELECT SCOPE_IDENTITY();", conn);
                ins.Parameters.AddWithValue("@L", model.Login.Trim());
                ins.Parameters.AddWithValue("@H", hash);
                ins.Parameters.AddWithValue("@R", model.RoleID);
                ins.Parameters.AddWithValue("@E", (object?)model.Email?.Trim() ?? DBNull.Value);
                int newUID = Convert.ToInt32(ins.ExecuteScalar());

                // Создаём профиль в зависимости от роли
                if (model.RoleID == 3) // Преподаватель
                {
                    using var insT = new SqlCommand(@"
                        INSERT INTO Teachers (UserID, FirstName, LastName, Patronymic, Email)
                        VALUES (@UID, @F, @L, @P, @E)", conn);
                    insT.Parameters.AddWithValue("@UID", newUID);
                    insT.Parameters.AddWithValue("@F", model.FirstName.Trim());
                    insT.Parameters.AddWithValue("@L", model.LastName.Trim());
                    insT.Parameters.AddWithValue("@P", model.Patronymic?.Trim() ?? "");
                    insT.Parameters.AddWithValue("@E", model.Email?.Trim() ?? "");
                    insT.ExecuteNonQuery();
                }
                else if (model.RoleID == 2) // Методист
                {
                    using var insM = new SqlCommand(@"
                        INSERT INTO Methodists (UserID, FirstName, LastName, Patronymic, Email)
                        VALUES (@UID, @F, @L, @P, @E)", conn);
                    insM.Parameters.AddWithValue("@UID", newUID);
                    insM.Parameters.AddWithValue("@F", model.FirstName.Trim());
                    insM.Parameters.AddWithValue("@L", model.LastName.Trim());
                    insM.Parameters.AddWithValue("@P", model.Patronymic?.Trim() ?? "");
                    insM.Parameters.AddWithValue("@E", model.Email?.Trim() ?? "");
                    insM.ExecuteNonQuery();
                }
                // RoleID == 1 (Администратор) — профиль не создаётся

                AuditService.Log(0, model.Login, "Регистрация", "Users",
                    $"Зарегистрирован: {model.LastName} {model.FirstName}, роль ID={model.RoleID}", ip);

                TempData["RegisterSuccess"] =
                    $"Аккаунт «{model.Login}» создан. Войдите в систему.";
                return RedirectToAction("Login");
            }
            catch (Exception ex)
            {
                model.ErrorMessage = $"Ошибка: {ex.Message}";
                ViewBag.Roles = GetRoles();
                return View(model);
            }
        }

        // ── ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ─────────────────────────────────────────

        private async Task SignInUser(int userID, string login,
                                       string role, string fullName, int profileID)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userID.ToString()),
                new(ClaimTypes.Name,           login),
                new(ClaimTypes.Role,           role),
                new("FullName",                fullName),
                new("ProfileID",               profileID.ToString())
            };

            var principal = new ClaimsPrincipal(
                new ClaimsIdentity(claims,
                    CookieAuthenticationDefaults.AuthenticationScheme));

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme, principal);
        }

        // Загружает ФИО и ProfileID из профильной таблицы
        private string LoadFullName(SqlConnection conn, int userID,
                                     string role, out int profileID)
        {
            profileID = 0;

            // Роль 1 = Администратор — у него нет профильной таблицы
            string? query = role switch
            {
                "Преподаватель" =>
                    "SELECT TeacherID, LastName + ' ' + FirstName FROM Teachers WHERE UserID=@UID",
                "Методист" =>
                    "SELECT MethodistID, LastName + ' ' + FirstName FROM Methodists WHERE UserID=@UID",
                _ => null
            };

            if (query == null) return "Администратор";

            using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@UID", userID);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                profileID = (int)r[0];
                return r[1].ToString()!.Trim();
            }
            return role;
        }

        // Email берём из таблицы Users (единый источник для всех ролей)
        private string? GetUserEmail(SqlConnection conn, int userID)
        {
            using var cmd = new SqlCommand(
                "SELECT ISNULL(Email,'') FROM Users WHERE UserID=@ID", conn);
            cmd.Parameters.AddWithValue("@ID", userID);
            var result = cmd.ExecuteScalar()?.ToString();
            return string.IsNullOrEmpty(result) ? null : result;
        }

        private List<(int ID, string Name)> GetRoles()
        {
            var list = new List<(int, string)>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                // Не показываем Администратора в форме регистрации
                using var cmd = new SqlCommand(
                    "SELECT RoleID, RoleName FROM Roles WHERE RoleID > 1 ORDER BY RoleID", conn);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(((int)r["RoleID"], r["RoleName"].ToString()!));
            }
            catch { }
            return list;
        }

        private static string MaskEmail(string email)
        {
            if (string.IsNullOrEmpty(email)) return "";
            int atIndex = email.IndexOf('@');
            if (atIndex <= 2) return email;
            return email[..2] + "***" + email[atIndex..];
        }

        private static string HashPassword(string password)
        {
            using var sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes).ToLower();
        }
    }
}