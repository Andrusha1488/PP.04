using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using StudentDiaryWeb.Data;
using StudentDiaryWeb.Models;
using StudentDiaryWeb.Services;
using System.Security.Claims;

namespace StudentDiaryWeb.Controllers
{
    [Authorize(Roles = "Администратор")]
    public class AdminController : Controller
    {
        private int CurrentUserID => int.Parse(
            User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
        private string CurrentLogin => User.Identity?.Name ?? "";
        private string CurrentIP =>
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

        // ================================================================
        //  ГЛАВНАЯ
        // ================================================================

        public IActionResult Index()
        {
            var stats = new Dictionary<string, int>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                var queries = new Dictionary<string, string>
                {
                    ["Пользователей"] = "SELECT COUNT(*) FROM Users",
                    ["Преподавателей"] = "SELECT COUNT(*) FROM Teachers",
                    ["Методистов"] = "SELECT COUNT(*) FROM Methodists",
                    ["Групп"] = "SELECT COUNT(*) FROM Groups",
                    ["Студентов"] = "SELECT COUNT(*) FROM Students WHERE IsActive=1",
                    ["Предметов"] = "SELECT COUNT(*) FROM Subjects",
                    ["Назначений"] = "SELECT COUNT(*) FROM TeacherAssignments",
                    ["Занятий"] = "SELECT COUNT(*) FROM Lessons"
                };
                foreach (var q in queries)
                {
                    using var cmd = new SqlCommand(q.Value, conn);
                    stats[q.Key] = (int)cmd.ExecuteScalar();
                }
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return View(stats);
        }

        // ================================================================
        //  ПОЛЬЗОВАТЕЛИ — фильтр по роли и статусу
        // ================================================================

        public IActionResult Users(int? roleId, string? status)
        {
            var list = new List<AppUser>();
            var roles = new List<(int ID, string Name)>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();

                // Загружаем роли для фильтра
                using (var cmd = new SqlCommand(
                    "SELECT RoleID, RoleName FROM Roles ORDER BY RoleID", conn))
                {
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        roles.Add(((int)r["RoleID"], r["RoleName"].ToString()!));
                }

                // Строим WHERE
                var conditions = new List<string>();
                if (roleId.HasValue) conditions.Add("u.RoleID = @RoleID");
                if (status == "active") conditions.Add("u.IsActive = 1");
                if (status == "blocked") conditions.Add("u.IsActive = 0");
                string where = conditions.Any()
                    ? "WHERE " + string.Join(" AND ", conditions) : "";

                using var cmd2 = new SqlCommand($@"
                    SELECT u.UserID, u.Login, r.RoleName, u.RoleID,
                           u.IsActive, ISNULL(u.Email,'') AS Email
                    FROM Users u
                    JOIN Roles r ON u.RoleID = r.RoleID
                    {where}
                    ORDER BY r.RoleID, u.Login", conn);
                if (roleId.HasValue)
                    cmd2.Parameters.AddWithValue("@RoleID", roleId.Value);
                using var r2 = cmd2.ExecuteReader();
                while (r2.Read())
                    list.Add(new AppUser
                    {
                        UserID = (int)r2["UserID"],
                        Login = r2["Login"].ToString()!,
                        RoleName = r2["RoleName"].ToString()!,
                        RoleID = (int)r2["RoleID"],
                        IsActive = (bool)r2["IsActive"]
                    });
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }

            ViewBag.Roles = roles;
            ViewBag.SelectedRole = roleId;
            ViewBag.SelectedStatus = status;
            return View(list);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult ToggleUser(int id)
        {
            if (id == CurrentUserID)
            {
                TempData["Error"] = "Нельзя заблокировать собственный аккаунт.";
                return RedirectToAction("Users");
            }
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(
                    "UPDATE Users SET IsActive = 1 - IsActive WHERE UserID=@ID", conn);
                cmd.Parameters.AddWithValue("@ID", id);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin,
                    "Блокировка/разблокировка", "Users",
                    $"Изменён статус пользователя ID={id}", CurrentIP);
                TempData["Success"] = "Статус пользователя изменён.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Users");
        }

        [HttpGet]
        public IActionResult CreateUser()
        {
            ViewBag.Roles = GetRoles();
            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult CreateUser(string login, string password, int roleID,
            string? firstName, string? lastName, string? patronymic, string? email)
        {
            if (string.IsNullOrWhiteSpace(login) || login.Length < 3)
            {
                TempData["Error"] = "Логин должен содержать не менее 3 символов.";
                ViewBag.Roles = GetRoles();
                return View();
            }
            if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            {
                TempData["Error"] = "Пароль должен содержать не менее 6 символов.";
                ViewBag.Roles = GetRoles();
                return View();
            }

            try
            {
                using var conn = DatabaseHelper.GetConnection();

                // Проверяем что логин не занят
                using (var chk = new SqlCommand(
                    "SELECT COUNT(*) FROM Users WHERE Login = @L", conn))
                {
                    chk.Parameters.AddWithValue("@L", login.Trim());
                    if ((int)chk.ExecuteScalar() > 0)
                    {
                        TempData["Error"] = $"Пользователь с логином «{login}» уже существует.";
                        ViewBag.Roles = GetRoles();
                        return View();
                    }
                }

                // Хешируем пароль (SHA-256)
                string passwordHash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(password);
                    var hash = sha.ComputeHash(bytes);
                    passwordHash = BitConverter.ToString(hash).Replace("-", "").ToLower();
                }

                // Создаём пользователя
                int newUserID;
                using (var cmd = new SqlCommand(@"
            INSERT INTO Users (Login, PasswordHash, RoleID, IsActive, Email)
            OUTPUT INSERTED.UserID
            VALUES (@L, @P, @R, 1, @E)", conn))
                {
                    cmd.Parameters.AddWithValue("@L", login.Trim());
                    cmd.Parameters.AddWithValue("@P", passwordHash);
                    cmd.Parameters.AddWithValue("@R", roleID);
                    cmd.Parameters.AddWithValue("@E", (object?)(email?.Trim()) ?? DBNull.Value);
                    newUserID = (int)cmd.ExecuteScalar();
                }

                // Если роль Преподаватель (3) — создаём запись в Teachers
                if (roleID == 3)
                {
                    using var ins = new SqlCommand(@"
                INSERT INTO Teachers (UserID, FirstName, LastName, Patronymic, Email)
                VALUES (@UID, @F, @L2, @P2, @E2)", conn);
                    ins.Parameters.AddWithValue("@UID", newUserID);
                    ins.Parameters.AddWithValue("@F", firstName?.Trim() ?? "");
                    ins.Parameters.AddWithValue("@L2", lastName?.Trim() ?? "");
                    ins.Parameters.AddWithValue("@P2", patronymic?.Trim() ?? "");
                    ins.Parameters.AddWithValue("@E2", email?.Trim() ?? "");
                    ins.ExecuteNonQuery();
                }
                // Если роль Методист (2) — создаём запись в Methodists
                else if (roleID == 2)
                {
                    using var ins = new SqlCommand(@"
                INSERT INTO Methodists (UserID, FirstName, LastName, Patronymic, Email)
                VALUES (@UID, @F, @L2, @P2, @E2)", conn);
                    ins.Parameters.AddWithValue("@UID", newUserID);
                    ins.Parameters.AddWithValue("@F", firstName?.Trim() ?? "");
                    ins.Parameters.AddWithValue("@L2", lastName?.Trim() ?? "");
                    ins.Parameters.AddWithValue("@P2", patronymic?.Trim() ?? "");
                    ins.Parameters.AddWithValue("@E2", email?.Trim() ?? "");
                    ins.ExecuteNonQuery();
                }

                AuditService.Log(CurrentUserID, CurrentLogin, "Создание", "Users",
                    $"Создан пользователь «{login}», роль ID={roleID}", CurrentIP);
                TempData["Success"] = $"Пользователь «{login}» успешно создан.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }

            return RedirectToAction("Users");
        }

        private List<(int ID, string Name)> GetRoles()
        {
            var list = new List<(int, string)>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(
                    "SELECT RoleID, RoleName FROM Roles ORDER BY RoleID", conn);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(((int)r["RoleID"], r["RoleName"].ToString()!));
            }
            catch { }
            return list;
        }

        [HttpGet]
        public IActionResult ChangeRole(int id)
        {
            if (id == CurrentUserID)
            {
                TempData["Error"] = "Нельзя изменить роль собственного аккаунта.";
                return RedirectToAction("Users");
            }
            AppUser? model = null;
            var roles = new List<(int ID, string Name)>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmdU = new SqlCommand(@"
                    SELECT u.UserID, u.Login, r.RoleName, u.RoleID, u.IsActive
                    FROM Users u JOIN Roles r ON u.RoleID = r.RoleID
                    WHERE u.UserID = @ID", conn);
                cmdU.Parameters.AddWithValue("@ID", id);
                using var r = cmdU.ExecuteReader();
                if (r.Read())
                    model = new AppUser
                    {
                        UserID = (int)r["UserID"],
                        Login = r["Login"].ToString()!,
                        RoleName = r["RoleName"].ToString()!,
                        RoleID = (int)r["RoleID"],
                        IsActive = (bool)r["IsActive"]
                    };
                r.Close();
                using var cmdR = new SqlCommand(
                    "SELECT RoleID, RoleName FROM Roles ORDER BY RoleID", conn);
                using var r2 = cmdR.ExecuteReader();
                while (r2.Read())
                    roles.Add(((int)r2["RoleID"], r2["RoleName"].ToString()!));
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            ViewBag.Roles = roles;
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult ChangeRole(int userID, int newRoleID,
            string firstName, string lastName, string patronymic)
        {
            if (userID == CurrentUserID)
            {
                TempData["Error"] = "Нельзя изменить роль собственного аккаунта.";
                return RedirectToAction("Users");
            }
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var getOld = new SqlCommand(
                    "SELECT RoleID, Login FROM Users WHERE UserID=@ID", conn);
                getOld.Parameters.AddWithValue("@ID", userID);
                using var rr = getOld.ExecuteReader();
                if (!rr.Read()) return RedirectToAction("Users");
                int oldRoleID = (int)rr["RoleID"];
                string login = rr["Login"].ToString()!;
                rr.Close();

                using var upd = new SqlCommand(
                    "UPDATE Users SET RoleID=@R WHERE UserID=@ID", conn);
                upd.Parameters.AddWithValue("@R", newRoleID);
                upd.Parameters.AddWithValue("@ID", userID);
                upd.ExecuteNonQuery();

                if (oldRoleID != newRoleID)
                {
                    if (oldRoleID == 3)
                    {
                        using var del = new SqlCommand(
                            "DELETE FROM Teachers WHERE UserID=@ID", conn);
                        del.Parameters.AddWithValue("@ID", userID); del.ExecuteNonQuery();
                    }
                    else if (oldRoleID == 2)
                    {
                        using var del = new SqlCommand(
                            "DELETE FROM Methodists WHERE UserID=@ID", conn);
                        del.Parameters.AddWithValue("@ID", userID); del.ExecuteNonQuery();
                    }
                }
                if (newRoleID == 3)
                {
                    using var chk = new SqlCommand(
                        "SELECT COUNT(*) FROM Teachers WHERE UserID=@ID", conn);
                    chk.Parameters.AddWithValue("@ID", userID);
                    if ((int)chk.ExecuteScalar() == 0)
                    {
                        using var ins = new SqlCommand(@"
                            INSERT INTO Teachers (UserID,FirstName,LastName,Patronymic,Email)
                            VALUES (@UID,@F,@L,@P,'')", conn);
                        ins.Parameters.AddWithValue("@UID", userID);
                        ins.Parameters.AddWithValue("@F", firstName?.Trim() ?? "");
                        ins.Parameters.AddWithValue("@L", lastName?.Trim() ?? "");
                        ins.Parameters.AddWithValue("@P", patronymic?.Trim() ?? "");
                        ins.ExecuteNonQuery();
                    }
                }
                else if (newRoleID == 2)
                {
                    using var chk = new SqlCommand(
                        "SELECT COUNT(*) FROM Methodists WHERE UserID=@ID", conn);
                    chk.Parameters.AddWithValue("@ID", userID);
                    if ((int)chk.ExecuteScalar() == 0)
                    {
                        using var ins = new SqlCommand(@"
                            INSERT INTO Methodists (UserID,FirstName,LastName,Patronymic,Email)
                            VALUES (@UID,@F,@L,@P,'')", conn);
                        ins.Parameters.AddWithValue("@UID", userID);
                        ins.Parameters.AddWithValue("@F", firstName?.Trim() ?? "");
                        ins.Parameters.AddWithValue("@L", lastName?.Trim() ?? "");
                        ins.Parameters.AddWithValue("@P", patronymic?.Trim() ?? "");
                        ins.ExecuteNonQuery();
                    }
                }
                AuditService.Log(CurrentUserID, CurrentLogin, "Смена роли", "Users",
                    $"Пользователю {login} изменена роль с {oldRoleID} на {newRoleID}", CurrentIP);
                TempData["Success"] = $"Роль пользователя «{login}» изменена.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Users");
        }

        // ================================================================
        //  ГРУППЫ
        // ================================================================

        public IActionResult Groups(int? filterCourse)
        {
            var list = new List<Group>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                string where = filterCourse.HasValue ? "WHERE g.CourseYear = @Course" : "";
                using var cmd = new SqlCommand($@"
            SELECT g.GroupID, g.GroupName, g.CourseYear,
                   ISNULL(g.Specialty,'') AS Specialty,
                   COUNT(s.StudentID) AS StudentCount
            FROM Groups g
            LEFT JOIN Students s ON g.GroupID = s.GroupID AND s.IsActive = 1
            {where}
            GROUP BY g.GroupID, g.GroupName, g.CourseYear, g.Specialty
            ORDER BY g.CourseYear, g.GroupName", conn);
                if (filterCourse.HasValue)
                    cmd.Parameters.AddWithValue("@Course", filterCourse.Value);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var g = new Group
                    {
                        GroupID = (int)r["GroupID"],
                        GroupName = r["GroupName"].ToString()!,
                        CourseYear = (byte)r["CourseYear"],
                        Specialty = r["Specialty"].ToString()!
                    };
                    ViewData[$"count_{g.GroupID}"] = (int)r["StudentCount"];
                    list.Add(g);
                }
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            ViewBag.FilterCourse = filterCourse ?? 0;
            return View(list);
        }

        [HttpGet] public IActionResult CreateGroup() => View(new Group());

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult CreateGroup(Group model)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(
                    "INSERT INTO Groups (GroupName,CourseYear,Specialty) VALUES (@N,@C,@S)", conn);
                cmd.Parameters.AddWithValue("@N", model.GroupName.Trim());
                cmd.Parameters.AddWithValue("@C", model.CourseYear);
                cmd.Parameters.AddWithValue("@S", model.Specialty?.Trim() ?? "");
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Создание", "Groups",
                    $"Группа: {model.GroupName}", CurrentIP);
                TempData["Success"] = "Группа создана.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Groups");
        }

        [HttpGet]
        public IActionResult EditGroup(int id)
        {
            Group? model = null;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(@"
                    SELECT GroupID, GroupName, CourseYear,
                           ISNULL(Specialty,'') AS Specialty
                    FROM Groups WHERE GroupID=@ID", conn);
                cmd.Parameters.AddWithValue("@ID", id);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                    model = new Group
                    {
                        GroupID = (int)r["GroupID"],
                        GroupName = r["GroupName"].ToString()!,
                        CourseYear = (byte)r["CourseYear"],
                        Specialty = r["Specialty"].ToString()!
                    };
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult EditGroup(Group model)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(@"
                    UPDATE Groups SET GroupName=@N, CourseYear=@C, Specialty=@S
                    WHERE GroupID=@ID", conn);
                cmd.Parameters.AddWithValue("@N", model.GroupName.Trim());
                cmd.Parameters.AddWithValue("@C", model.CourseYear);
                cmd.Parameters.AddWithValue("@S", model.Specialty?.Trim() ?? "");
                cmd.Parameters.AddWithValue("@ID", model.GroupID);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Изменение", "Groups",
                    $"Группа ID={model.GroupID}: {model.GroupName}", CurrentIP);
                TempData["Success"] = "Группа обновлена.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Groups");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult DeleteGroup(int id)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(
                    "DELETE FROM Groups WHERE GroupID=@ID", conn);
                cmd.Parameters.AddWithValue("@ID", id);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Удаление", "Groups",
                    $"Группа ID={id}", CurrentIP);
                TempData["Success"] = "Группа удалена.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Groups");
        }

        // ================================================================
        //  СТУДЕНТЫ — фильтр по группе, статусу и поиск по ФИО
        // ================================================================

        public IActionResult Students(int? groupId, string? status, string? search)
        {
            ViewBag.Groups = GetGroups();
            ViewBag.SelectedGroup = groupId;
            ViewBag.SelectedStatus = status;
            ViewBag.Search = search;
            var list = new List<Student>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                var conditions = new List<string>();
                if (groupId.HasValue) conditions.Add("s.GroupID = @GID");
                if (status == "active") conditions.Add("s.IsActive = 1");
                if (status == "fired") conditions.Add("s.IsActive = 0");
                if (!string.IsNullOrWhiteSpace(search))
                    conditions.Add("(s.LastName + ' ' + s.FirstName + ' ' + s.Patronymic LIKE @Search)");
                string where = conditions.Any()
                    ? "WHERE " + string.Join(" AND ", conditions) : "";

                using var cmd = new SqlCommand($@"
                    SELECT s.StudentID, s.LastName, s.FirstName,
                           ISNULL(s.Patronymic,'') AS Patronymic,
                           s.GroupID, g.GroupName,
                           s.EnrollmentDate, s.IsActive
                    FROM Students s
                    JOIN Groups g ON s.GroupID = g.GroupID
                    {where}
                    ORDER BY g.GroupName, s.LastName, s.FirstName", conn);

                if (groupId.HasValue)
                    cmd.Parameters.AddWithValue("@GID", groupId.Value);
                if (!string.IsNullOrWhiteSpace(search))
                    cmd.Parameters.AddWithValue("@Search", $"%{search}%");

                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new Student
                    {
                        StudentID = (int)r["StudentID"],
                        LastName = r["LastName"].ToString()!,
                        FirstName = r["FirstName"].ToString()!,
                        Patronymic = r["Patronymic"].ToString()!,
                        GroupID = (int)r["GroupID"],
                        GroupName = r["GroupName"].ToString()!,
                        EnrollmentDate = (DateTime)r["EnrollmentDate"],
                        IsActive = (bool)r["IsActive"]
                    });
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return View(list);
        }

        [HttpGet]
        public IActionResult CreateStudent()
        {
            ViewBag.Groups = GetGroups();
            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult CreateStudent(Student model)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(@"
                    INSERT INTO Students
                        (LastName, FirstName, Patronymic, GroupID, EnrollmentDate, IsActive)
                    VALUES (@L, @F, @P, @G, @D, 1)", conn);
                cmd.Parameters.AddWithValue("@L", model.LastName.Trim());
                cmd.Parameters.AddWithValue("@F", model.FirstName.Trim());
                cmd.Parameters.AddWithValue("@P", model.Patronymic?.Trim() ?? "");
                cmd.Parameters.AddWithValue("@G", model.GroupID);
                cmd.Parameters.AddWithValue("@D", model.EnrollmentDate);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Создание", "Students",
                    $"{model.LastName} {model.FirstName}", CurrentIP);
                TempData["Success"] = "Студент добавлен.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Students");
        }

        [HttpGet]
        public IActionResult EditStudent(int id)
        {
            ViewBag.Groups = GetGroups();
            Student? model = null;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(@"
                    SELECT s.StudentID, s.LastName, s.FirstName,
                           ISNULL(s.Patronymic,'') AS Patronymic,
                           s.GroupID, g.GroupName, s.EnrollmentDate, s.IsActive
                    FROM Students s JOIN Groups g ON s.GroupID=g.GroupID
                    WHERE s.StudentID=@ID", conn);
                cmd.Parameters.AddWithValue("@ID", id);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                    model = new Student
                    {
                        StudentID = (int)r["StudentID"],
                        LastName = r["LastName"].ToString()!,
                        FirstName = r["FirstName"].ToString()!,
                        Patronymic = r["Patronymic"].ToString()!,
                        GroupID = (int)r["GroupID"],
                        GroupName = r["GroupName"].ToString()!,
                        EnrollmentDate = (DateTime)r["EnrollmentDate"],
                        IsActive = (bool)r["IsActive"]
                    };
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult EditStudent(Student model)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(@"
                    UPDATE Students
                    SET LastName=@L, FirstName=@F, Patronymic=@P,
                        GroupID=@G, EnrollmentDate=@D, IsActive=@A
                    WHERE StudentID=@ID", conn);
                cmd.Parameters.AddWithValue("@L", model.LastName.Trim());
                cmd.Parameters.AddWithValue("@F", model.FirstName.Trim());
                cmd.Parameters.AddWithValue("@P", model.Patronymic?.Trim() ?? "");
                cmd.Parameters.AddWithValue("@G", model.GroupID);
                cmd.Parameters.AddWithValue("@D", model.EnrollmentDate);
                cmd.Parameters.AddWithValue("@A", model.IsActive);
                cmd.Parameters.AddWithValue("@ID", model.StudentID);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Изменение", "Students",
                    $"Студент ID={model.StudentID}: {model.LastName} {model.FirstName}", CurrentIP);
                TempData["Success"] = "Данные студента обновлены.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Students");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult DeleteStudent(int id)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(
                    "DELETE FROM Students WHERE StudentID=@ID", conn);
                cmd.Parameters.AddWithValue("@ID", id);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Удаление", "Students",
                    $"Студент ID={id}", CurrentIP);
                TempData["Success"] = "Студент удалён.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Students");
        }

        // ================================================================
        //  ПРЕДМЕТЫ
        // ================================================================

        public IActionResult Subjects(string? search, int? minHours, int? maxHours)
        {
            var list = new List<Subject>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                var conditions = new List<string>();
                if (!string.IsNullOrWhiteSpace(search))
                    conditions.Add("(SubjectName LIKE @S OR ISNULL(Description,'') LIKE @S)");
                if (minHours.HasValue) conditions.Add("HoursTotal >= @MinH");
                if (maxHours.HasValue) conditions.Add("HoursTotal <= @MaxH");
                string where = conditions.Any()
                    ? "WHERE " + string.Join(" AND ", conditions) : "";

                using var cmd = new SqlCommand($@"
            SELECT SubjectID, SubjectName,
                   ISNULL(Description,'') AS Description, HoursTotal
            FROM Subjects {where}
            ORDER BY SubjectName", conn);
                if (!string.IsNullOrWhiteSpace(search))
                    cmd.Parameters.AddWithValue("@S", $"%{search}%");
                if (minHours.HasValue) cmd.Parameters.AddWithValue("@MinH", minHours.Value);
                if (maxHours.HasValue) cmd.Parameters.AddWithValue("@MaxH", maxHours.Value);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new Subject
                    {
                        SubjectID = (int)r["SubjectID"],
                        SubjectName = r["SubjectName"].ToString()!,
                        Description = r["Description"].ToString()!,
                        HoursTotal = (short)r["HoursTotal"]
                    });
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            ViewBag.Search = search ?? "";
            ViewBag.MinHours = minHours ?? 0;
            ViewBag.MaxHours = maxHours ?? 9999;
            return View(list);
        }

        [HttpGet] public IActionResult CreateSubject() => View(new Subject());

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult CreateSubject(Subject model)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(
                    "INSERT INTO Subjects (SubjectName,Description,HoursTotal) VALUES (@N,@D,@H)", conn);
                cmd.Parameters.AddWithValue("@N", model.SubjectName.Trim());
                cmd.Parameters.AddWithValue("@D", model.Description?.Trim() ?? "");
                cmd.Parameters.AddWithValue("@H", model.HoursTotal);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Создание", "Subjects",
                    $"{model.SubjectName}", CurrentIP);
                TempData["Success"] = "Предмет добавлен.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Subjects");
        }

        [HttpGet]
        public IActionResult EditSubject(int id)
        {
            Subject? model = null;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(@"
                    SELECT SubjectID, SubjectName,
                           ISNULL(Description,'') AS Description, HoursTotal
                    FROM Subjects WHERE SubjectID=@ID", conn);
                cmd.Parameters.AddWithValue("@ID", id);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                    model = new Subject
                    {
                        SubjectID = (int)r["SubjectID"],
                        SubjectName = r["SubjectName"].ToString()!,
                        Description = r["Description"].ToString()!,
                        HoursTotal = (short)r["HoursTotal"]
                    };
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult EditSubject(Subject model)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(@"
                    UPDATE Subjects SET SubjectName=@N, Description=@D, HoursTotal=@H
                    WHERE SubjectID=@ID", conn);
                cmd.Parameters.AddWithValue("@N", model.SubjectName.Trim());
                cmd.Parameters.AddWithValue("@D", model.Description?.Trim() ?? "");
                cmd.Parameters.AddWithValue("@H", model.HoursTotal);
                cmd.Parameters.AddWithValue("@ID", model.SubjectID);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Изменение", "Subjects",
                    $"Предмет ID={model.SubjectID}: {model.SubjectName}", CurrentIP);
                TempData["Success"] = "Предмет обновлён.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Subjects");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult DeleteSubject(int id)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(
                    "DELETE FROM Subjects WHERE SubjectID=@ID", conn);
                cmd.Parameters.AddWithValue("@ID", id);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Удаление", "Subjects",
                    $"Предмет ID={id}", CurrentIP);
                TempData["Success"] = "Предмет удалён.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Subjects");
        }

        // ================================================================
        //  СЕМЕСТРЫ
        // ================================================================

        public IActionResult Semesters(string? filterStatus, int? filterYear)
        {
            var list = new List<Semester>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                var conditions = new List<string>();
                if (filterStatus == "current") conditions.Add("IsCurrent = 1");
                if (filterStatus == "archive") conditions.Add("IsCurrent = 0");
                if (filterYear.HasValue && filterYear.Value > 0) conditions.Add("AcademicYear = @Year");
                string where = conditions.Any()
                    ? "WHERE " + string.Join(" AND ", conditions) : "";

                using var cmd = new SqlCommand($@"
            SELECT SemesterID, SemesterName, StartDate, EndDate,
                   AcademicYear, IsCurrent
            FROM Semesters {where}
            ORDER BY StartDate DESC", conn);
                if (filterYear.HasValue && filterYear.Value > 0)
                    cmd.Parameters.AddWithValue("@Year", filterYear.Value);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new Semester
                    {
                        SemesterID = (int)r["SemesterID"],
                        SemesterName = r["SemesterName"].ToString()!,
                        StartDate = (DateTime)r["StartDate"],
                        EndDate = (DateTime)r["EndDate"],
                        AcademicYear = (short)r["AcademicYear"],
                        IsCurrent = (bool)r["IsCurrent"]
                    });
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            ViewBag.FilterStatus = filterStatus ?? "";
            ViewBag.FilterYear = filterYear ?? 0;
            return View(list);
        }

        [HttpGet]
        public IActionResult CreateSemester() =>
            View(new Semester { StartDate = DateTime.Today, EndDate = DateTime.Today.AddMonths(4) });

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult CreateSemester(Semester model)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(@"
                    INSERT INTO Semesters (SemesterName,StartDate,EndDate,AcademicYear,IsCurrent)
                    VALUES (@N,@S,@E,@Y,0)", conn);
                cmd.Parameters.AddWithValue("@N", model.SemesterName.Trim());
                cmd.Parameters.AddWithValue("@S", model.StartDate);
                cmd.Parameters.AddWithValue("@E", model.EndDate);
                cmd.Parameters.AddWithValue("@Y", model.AcademicYear);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Создание", "Semesters",
                    $"{model.SemesterName}", CurrentIP);
                TempData["Success"] = "Семестр создан.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Semesters");
        }

        [HttpGet]
        public IActionResult EditSemester(int id)
        {
            Semester? model = null;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(@"
                    SELECT SemesterID, SemesterName, StartDate, EndDate,
                           AcademicYear, IsCurrent
                    FROM Semesters WHERE SemesterID=@ID", conn);
                cmd.Parameters.AddWithValue("@ID", id);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                    model = new Semester
                    {
                        SemesterID = (int)r["SemesterID"],
                        SemesterName = r["SemesterName"].ToString()!,
                        StartDate = (DateTime)r["StartDate"],
                        EndDate = (DateTime)r["EndDate"],
                        AcademicYear = (short)r["AcademicYear"],
                        IsCurrent = (bool)r["IsCurrent"]
                    };
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult EditSemester(Semester model)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(@"
                    UPDATE Semesters
                    SET SemesterName=@N, StartDate=@S, EndDate=@E,
                        AcademicYear=@Y, IsCurrent=@C
                    WHERE SemesterID=@ID", conn);
                cmd.Parameters.AddWithValue("@N", model.SemesterName.Trim());
                cmd.Parameters.AddWithValue("@S", model.StartDate);
                cmd.Parameters.AddWithValue("@E", model.EndDate);
                cmd.Parameters.AddWithValue("@Y", model.AcademicYear);
                cmd.Parameters.AddWithValue("@C", model.IsCurrent);
                cmd.Parameters.AddWithValue("@ID", model.SemesterID);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Изменение", "Semesters",
                    $"Семестр ID={model.SemesterID}", CurrentIP);
                TempData["Success"] = "Семестр обновлён.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Semesters");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SetCurrentSemester(int id)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(@"
                    UPDATE Semesters SET IsCurrent=0;
                    UPDATE Semesters SET IsCurrent=1 WHERE SemesterID=@ID;", conn);
                cmd.Parameters.AddWithValue("@ID", id);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Изменение", "Semesters",
                    $"Текущий семестр = ID={id}", CurrentIP);
                TempData["Success"] = "Текущий семестр установлен.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Semesters");
        }

        // ================================================================
        //  НАЗНАЧЕНИЯ — фильтр по семестру и преподавателю
        // ================================================================

        public IActionResult Assignments(int? semesterId, int? teacherId)
        {
            var list = new List<TeacherAssignment>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                var conditions = new List<string>();
                if (semesterId.HasValue) conditions.Add("ta.SemesterID = @SemID");
                if (teacherId.HasValue) conditions.Add("ta.TeacherID  = @TchID");
                string where = conditions.Any()
                    ? "WHERE " + string.Join(" AND ", conditions) : "";

                using var cmd = new SqlCommand($@"
                    SELECT ta.AssignmentID,
                           t.LastName + ' ' + t.FirstName AS TeacherName,
                           sub.SubjectName, g.GroupName,
                           sem.SemesterName, sem.IsCurrent,
                           sub.HoursTotal,
                           ISNULL((SELECT SUM(l.HoursCount)
                                   FROM Lessons l
                                   WHERE l.AssignmentID=ta.AssignmentID),0) AS HoursConducted
                    FROM TeacherAssignments ta
                    JOIN Teachers  t   ON ta.TeacherID  = t.TeacherID
                    JOIN Subjects  sub ON ta.SubjectID  = sub.SubjectID
                    JOIN Groups    g   ON ta.GroupID    = g.GroupID
                    JOIN Semesters sem ON ta.SemesterID = sem.SemesterID
                    {where}
                    ORDER BY sem.IsCurrent DESC, sem.StartDate DESC,
                             g.GroupName, sub.SubjectName", conn);
                if (semesterId.HasValue)
                    cmd.Parameters.AddWithValue("@SemID", semesterId.Value);
                if (teacherId.HasValue)
                    cmd.Parameters.AddWithValue("@TchID", teacherId.Value);

                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new TeacherAssignment
                    {
                        AssignmentID = (int)r["AssignmentID"],
                        TeacherName = r["TeacherName"].ToString()!,
                        SubjectName = r["SubjectName"].ToString()!,
                        GroupName = r["GroupName"].ToString()!,
                        SemesterName = r["SemesterName"].ToString()!,
                        HoursTotal = (int)(short)r["HoursTotal"],
                        HoursConducted = (int)r["HoursConducted"]
                    });
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }

            ViewBag.Semesters = GetSemesters();
            ViewBag.Teachers = GetTeachers();
            ViewBag.SelectedSemester = semesterId;
            ViewBag.SelectedTeacher = teacherId;
            return View(list);
        }

        [HttpGet]
        public IActionResult CreateAssignment()
        {
            ViewBag.Teachers = GetTeachers();
            ViewBag.Subjects = GetSubjects();
            ViewBag.Groups = GetGroups();
            ViewBag.Semesters = GetSemesters();
            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult CreateAssignment(int teacherID, int subjectID,
            int groupID, int semesterID)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(@"
                    INSERT INTO TeacherAssignments (TeacherID,SubjectID,GroupID,SemesterID)
                    VALUES (@T,@S,@G,@Sem)", conn);
                cmd.Parameters.AddWithValue("@T", teacherID);
                cmd.Parameters.AddWithValue("@S", subjectID);
                cmd.Parameters.AddWithValue("@G", groupID);
                cmd.Parameters.AddWithValue("@Sem", semesterID);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin,
                    "Создание", "TeacherAssignments",
                    $"Назначение: учитель={teacherID}, предмет={subjectID}, группа={groupID}",
                    CurrentIP);
                TempData["Success"] = "Назначение создано.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Assignments");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult DeleteAssignment(int id)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(
                    "DELETE FROM TeacherAssignments WHERE AssignmentID=@ID", conn);
                cmd.Parameters.AddWithValue("@ID", id);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin,
                    "Удаление", "TeacherAssignments", $"Назначение ID={id}", CurrentIP);
                TempData["Success"] = "Назначение удалено.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Assignments");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult LockFinalGrades(int assignmentId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(
                    "UPDATE FinalGrades SET IsLocked=1 WHERE AssignmentID=@AID", conn);
                cmd.Parameters.AddWithValue("@AID", assignmentId);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin,
                    "Блокировка", "FinalGrades",
                    $"Заблокированы итоговые для назначения ID={assignmentId}", CurrentIP);
                TempData["Success"] = "Итоговые оценки заблокированы.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Assignments");
        }

        // ================================================================
        //  ЖУРНАЛ АУДИТА — фильтр по пользователю, действию, дате
        // ================================================================

        public IActionResult AuditLog(string? search, string? dateFrom,
    string? dateTo, string? actionFilter, int page = 1)
        {
            int pageSize = 50;
            var list = new List<AuditLog>();
            int total = 0;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                var conditions = new List<string>();
                if (!string.IsNullOrWhiteSpace(search))
                    conditions.Add("(UserLogin LIKE @S OR Details LIKE @S)");
                if (!string.IsNullOrWhiteSpace(actionFilter))
                    conditions.Add("Action LIKE @Action");
                if (!string.IsNullOrWhiteSpace(dateFrom))
                    conditions.Add("CAST(CreatedAt AS DATE) >= @DateFrom");
                if (!string.IsNullOrWhiteSpace(dateTo))
                    conditions.Add("CAST(CreatedAt AS DATE) <= @DateTo");
                string where = conditions.Any()
                    ? "WHERE " + string.Join(" AND ", conditions) : "";

                using var cnt = new SqlCommand(
                    $"SELECT COUNT(*) FROM AuditLog {where}", conn);
                if (!string.IsNullOrWhiteSpace(search))
                    cnt.Parameters.AddWithValue("@S", $"%{search}%");
                if (!string.IsNullOrWhiteSpace(actionFilter))
                    cnt.Parameters.AddWithValue("@Action", $"%{actionFilter}%");
                if (!string.IsNullOrWhiteSpace(dateFrom))
                    cnt.Parameters.AddWithValue("@DateFrom", dateFrom);
                if (!string.IsNullOrWhiteSpace(dateTo))
                    cnt.Parameters.AddWithValue("@DateTo", dateTo);
                total = (int)cnt.ExecuteScalar();

                using var cmd = new SqlCommand($@"
            SELECT LogID, UserID, UserLogin, Action,
                   TableName, Details, IPAddress, CreatedAt
            FROM AuditLog
            {where}
            ORDER BY CreatedAt DESC
            OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY", conn);
                if (!string.IsNullOrWhiteSpace(search))
                    cmd.Parameters.AddWithValue("@S", $"%{search}%");
                if (!string.IsNullOrWhiteSpace(actionFilter))
                    cmd.Parameters.AddWithValue("@Action", $"%{actionFilter}%");
                if (!string.IsNullOrWhiteSpace(dateFrom))
                    cmd.Parameters.AddWithValue("@DateFrom", dateFrom);
                if (!string.IsNullOrWhiteSpace(dateTo))
                    cmd.Parameters.AddWithValue("@DateTo", dateTo);
                cmd.Parameters.AddWithValue("@Skip", (page - 1) * pageSize);
                cmd.Parameters.AddWithValue("@Take", pageSize);

                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new AuditLog
                    {
                        LogID = (int)r["LogID"],
                        UserID = (int)r["UserID"],
                        UserLogin = r["UserLogin"].ToString()!,
                        Action = r["Action"].ToString()!,
                        TableName = r["TableName"].ToString()!,
                        Details = r["Details"].ToString()!,
                        IPAddress = r["IPAddress"].ToString()!,
                        CreatedAt = (DateTime)r["CreatedAt"]
                    });
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }

            ViewBag.Search = search;
            ViewBag.DateFrom = dateFrom;
            ViewBag.DateTo = dateTo;
            ViewBag.ActionFilter = actionFilter;
            ViewBag.Page = page;
            ViewBag.PageSize = pageSize;
            ViewBag.TotalPages = (int)Math.Ceiling(total / (double)pageSize);
            return View(list);
        }

        // ================================================================
        //  ВСПОМОГАТЕЛЬНЫЕ
        // ================================================================

        private List<(int ID, string Name)> GetTeachers()
        {
            var list = new List<(int, string)>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(
                    "SELECT TeacherID, LastName+' '+FirstName FROM Teachers ORDER BY LastName", conn);
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add(((int)r[0], r[1].ToString()!));
            }
            catch { }
            return list;
        }

        private List<(int ID, string Name)> GetSubjects()
        {
            var list = new List<(int, string)>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(
                    "SELECT SubjectID, SubjectName FROM Subjects ORDER BY SubjectName", conn);
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add(((int)r[0], r[1].ToString()!));
            }
            catch { }
            return list;
        }

        private List<(int ID, string Name)> GetGroups()
        {
            var list = new List<(int, string)>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(
                    "SELECT GroupID, GroupName FROM Groups ORDER BY GroupName", conn);
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add(((int)r[0], r[1].ToString()!));
            }
            catch { }
            return list;
        }

        private List<(int ID, string Name)> GetSemesters()
        {
            var list = new List<(int, string)>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(
                    "SELECT SemesterID, SemesterName FROM Semesters ORDER BY StartDate DESC", conn);
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add(((int)r[0], r[1].ToString()!));
            }
            catch { }
            return list;
        }
    }
}