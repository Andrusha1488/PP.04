using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using StudentDiaryWeb.Data;
using StudentDiaryWeb.Models;
using StudentDiaryWeb.Services;
using System.Security.Claims;

namespace StudentDiaryWeb.Controllers
{
    [Authorize(Roles = "Методист")]
    public class MethodistController : Controller
    {
        private int MethodistID => int.Parse(User.FindFirst("ProfileID")?.Value ?? "0");
        private int CurrentUserID => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
        private string CurrentLogin => User.Identity?.Name ?? "";
        private string CurrentIP => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

        // ================================================================
        //  ГЛАВНАЯ с фильтрами
        // ================================================================

        public IActionResult Index(int? semesterId, int? teacherId)
        {
            var model = new MethodistDashboard();
            var semesters = new List<(int ID, string Name)>();
            var teachers = new List<(int ID, string Name)>();

            try
            {
                using var conn = DatabaseHelper.GetConnection();

                using (var cmd = new SqlCommand(
                    "SELECT SemesterID, SemesterName FROM Semesters ORDER BY StartDate DESC", conn))
                {
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        semesters.Add(((int)r["SemesterID"], r["SemesterName"].ToString()!));
                }

                using (var cmd = new SqlCommand(
                    "SELECT TeacherID, LastName+' '+FirstName AS TName FROM Teachers ORDER BY LastName", conn))
                {
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        teachers.Add(((int)r["TeacherID"], r["TName"].ToString()!));
                }

                model.AllAssignments = GetAllAssignments(conn, semesterId, teacherId);
                model.NotReviewedThisMonth = new List<TeacherAssignment>();
                model.BehindSchedule = new List<TeacherAssignment>();

                foreach (var a in model.AllAssignments)
                {
                    using var chk = new SqlCommand(@"
                        SELECT COUNT(*) FROM JournalReviews
                        WHERE AssignmentID=@AID
                          AND MONTH(ReviewDate)=MONTH(GETDATE())
                          AND YEAR(ReviewDate)=YEAR(GETDATE())", conn);
                    chk.Parameters.AddWithValue("@AID", a.AssignmentID);
                    if ((int)chk.ExecuteScalar() == 0)
                        model.NotReviewedThisMonth.Add(a);

                    if (a.HoursTotal > 0 && a.HoursConducted < a.HoursTotal * 0.9m)
                        model.BehindSchedule.Add(a);
                }
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }

            ViewBag.Semesters = semesters;
            ViewBag.Teachers = teachers;
            ViewBag.SelectedSemester = semesterId;
            ViewBag.SelectedTeacher = teacherId;
            return View(model);
        }

        // ================================================================
        //  ПРОСМОТР ЖУРНАЛА
        // ================================================================

        public IActionResult Journal(int assignmentId)
        {
            var model = new JournalViewModel();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                model.Assignment = GetAssignmentByID(conn, assignmentId);
                if (model.Assignment == null) { TempData["Error"] = "Журнал не найден."; return RedirectToAction("Index"); }

                model.Students = GetStudentsByGroup(conn, model.Assignment.GroupID);
                model.Lessons = GetLessonsByAssignment(conn, assignmentId);

                if (model.Lessons.Any())
                {
                    string inClause = string.Join(",", model.Lessons.Select(l => l.LessonID));
                    using var cmd = new SqlCommand($@"
                        SELECT je.EntryID, je.LessonID, je.StudentID,
                               je.Attendance, je.Grade, ISNULL(je.Comment,'') AS Comment, je.UpdatedAt
                        FROM JournalEntries je WHERE je.LessonID IN ({inClause})", conn);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        int sid = (int)r["StudentID"]; int lid = (int)r["LessonID"];
                        if (!model.Entries.ContainsKey(sid)) model.Entries[sid] = new Dictionary<int, JournalEntry>();
                        model.Entries[sid][lid] = new JournalEntry
                        {
                            EntryID = (int)r["EntryID"],
                            LessonID = lid,
                            StudentID = sid,
                            Attendance = r["Attendance"] == DBNull.Value ? null : r["Attendance"].ToString()!.Trim(),
                            Grade = r["Grade"] == DBNull.Value ? null : r["Grade"].ToString()!.Trim(),
                            Comment = r["Comment"].ToString()!,
                            UpdatedAt = (DateTime)r["UpdatedAt"]
                        };
                    }
                }

                foreach (var s in model.Students)
                {
                    int sid = s.StudentID;
                    var entries = model.Entries.ContainsKey(sid) ? model.Entries[sid].Values.ToList() : new List<JournalEntry>();
                    var grades = entries.Where(e => e.Grade != null && decimal.TryParse(e.Grade, out _)).Select(e => decimal.Parse(e.Grade!)).ToList();
                    model.AverageGrades[sid] = grades.Any() ? Math.Round(grades.Average(), 1) : (decimal?)null;
                    model.AbsenceCounts[sid] = entries.Count(e => e.Attendance != null);
                }

                if (model.Lessons.Any())
                {
                    string inClause = string.Join(",", model.Lessons.Select(l => l.LessonID));
                    using var cmd = new SqlCommand($@"
                        SELECT jc.CommentID, jc.LessonID, jc.CommentText, jc.CreatedAt,
                               m.LastName+' '+m.FirstName AS MethodistName
                        FROM JournalComments jc JOIN Methodists m ON jc.MethodistID=m.MethodistID
                        WHERE jc.LessonID IN ({inClause}) ORDER BY jc.CreatedAt", conn);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        int lid = (int)r["LessonID"];
                        if (!model.Comments.ContainsKey(lid)) model.Comments[lid] = new List<JournalComment>();
                        model.Comments[lid].Add(new JournalComment
                        {
                            CommentID = (int)r["CommentID"],
                            LessonID = lid,
                            CommentText = r["CommentText"].ToString()!,
                            MethodistName = r["MethodistName"].ToString()!,
                            CreatedAt = (DateTime)r["CreatedAt"]
                        });
                    }
                }

                using (var cmd = new SqlCommand(@"
                    SELECT TOP 1 jr.ReviewDate, jr.Note, m.LastName+' '+m.FirstName AS MethodistName
                    FROM JournalReviews jr JOIN Methodists m ON jr.MethodistID=m.MethodistID
                    WHERE jr.AssignmentID=@AID ORDER BY jr.ReviewDate DESC", conn))
                {
                    cmd.Parameters.AddWithValue("@AID", assignmentId);
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                        model.LastReview = new JournalReview
                        {
                            ReviewDate = (DateTime)r["ReviewDate"],
                            Note = r["Note"].ToString()!,
                            MethodistName = r["MethodistName"].ToString()!
                        };
                }
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return View(model);
        }

        // ================================================================
        //  ОТМЕТИТЬ «ПРОВЕРЕНО»
        // ================================================================

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult MarkReviewed(int assignmentId, string? note)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(@"
                    INSERT INTO JournalReviews (AssignmentID,MethodistID,ReviewDate,Note)
                    VALUES (@AID,@MID,GETDATE(),@Note)", conn);
                cmd.Parameters.AddWithValue("@AID", assignmentId);
                cmd.Parameters.AddWithValue("@MID", MethodistID);
                cmd.Parameters.AddWithValue("@Note", note?.Trim() ?? "");
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Проверка журнала", "JournalReviews",
                    $"Журнал назначения ID={assignmentId} проверен", CurrentIP);
                TempData["Success"] = "Журнал отмечен как проверенный.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Journal", new { assignmentId });
        }

        // ================================================================
        //  ЗАМЕЧАНИЯ
        // ================================================================

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult AddComment(int lessonId, int assignmentId, string commentText)
        {
            if (string.IsNullOrWhiteSpace(commentText))
            {
                TempData["Error"] = "Замечание не может быть пустым.";
                return RedirectToAction("Journal", new { assignmentId });
            }
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(@"
                    INSERT INTO JournalComments (LessonID,MethodistID,CommentText)
                    VALUES (@LID,@MID,@Text)", conn);
                cmd.Parameters.AddWithValue("@LID", lessonId);
                cmd.Parameters.AddWithValue("@MID", MethodistID);
                cmd.Parameters.AddWithValue("@Text", commentText.Trim());
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Замечание", "JournalComments",
                    $"Замечание к занятию ID={lessonId}", CurrentIP);
                TempData["Success"] = "Замечание добавлено.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Journal", new { assignmentId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult DeleteComment(int commentId, int assignmentId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(@"
                    DELETE FROM JournalComments WHERE CommentID=@ID AND MethodistID=@MID", conn);
                cmd.Parameters.AddWithValue("@ID", commentId);
                cmd.Parameters.AddWithValue("@MID", MethodistID);
                cmd.ExecuteNonQuery();
                TempData["Success"] = "Замечание удалено.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Journal", new { assignmentId });
        }

        // ================================================================
        //  СВОДНЫЙ ОТЧЁТ с фильтрами
        // ================================================================

        public IActionResult Report(int? semesterId, int? teacherId)
        {
            var model = new MethodistDashboard();
            var semesters = new List<(int ID, string Name)>();
            var teachers = new List<(int ID, string Name)>();

            try
            {
                using var conn = DatabaseHelper.GetConnection();

                using (var cmd = new SqlCommand(
                    "SELECT SemesterID, SemesterName FROM Semesters ORDER BY StartDate DESC", conn))
                {
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        semesters.Add(((int)r["SemesterID"], r["SemesterName"].ToString()!));
                }

                using (var cmd = new SqlCommand(
                    "SELECT TeacherID, LastName+' '+FirstName AS TName FROM Teachers ORDER BY LastName", conn))
                {
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        teachers.Add(((int)r["TeacherID"], r["TName"].ToString()!));
                }

                model.AllAssignments = GetAllAssignments(conn, semesterId, teacherId);
                model.NotReviewedThisMonth = new List<TeacherAssignment>();
                model.BehindSchedule = new List<TeacherAssignment>();

                foreach (var a in model.AllAssignments)
                {
                    using var chk = new SqlCommand(@"
                        SELECT COUNT(*) FROM JournalReviews
                        WHERE AssignmentID=@AID
                          AND MONTH(ReviewDate)=MONTH(GETDATE())
                          AND YEAR(ReviewDate)=YEAR(GETDATE())", conn);
                    chk.Parameters.AddWithValue("@AID", a.AssignmentID);
                    if ((int)chk.ExecuteScalar() == 0)
                        model.NotReviewedThisMonth.Add(a);

                    if (a.HoursTotal > 0 && a.HoursConducted < a.HoursTotal * 0.9m)
                        model.BehindSchedule.Add(a);
                }
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }

            ViewBag.Semesters = semesters;
            ViewBag.Teachers = teachers;
            ViewBag.SelectedSemester = semesterId;
            ViewBag.SelectedTeacher = teacherId;
            return View(model);
        }

        // ================================================================
        //  ВСПОМОГАТЕЛЬНЫЕ
        // ================================================================

        private List<TeacherAssignment> GetAllAssignments(SqlConnection conn,
            int? semesterId = null, int? teacherId = null)
        {
            var list = new List<TeacherAssignment>();
            var conditions = new List<string>();
            if (semesterId.HasValue) conditions.Add("ta.SemesterID = @SemID");
            if (teacherId.HasValue) conditions.Add("ta.TeacherID  = @TchID");
            string where = conditions.Any()
                ? "WHERE " + string.Join(" AND ", conditions) : "";

            using var cmd = new SqlCommand($@"
                SELECT ta.AssignmentID,
                       t.TeacherID,
                       t.LastName+' '+t.FirstName AS TName,
                       sub.SubjectName, sub.HoursTotal,
                       g.GroupID, g.GroupName,
                       sem.SemesterID, sem.SemesterName, sem.IsCurrent,
                       ISNULL((SELECT SUM(l.HoursCount) FROM Lessons l
                               WHERE l.AssignmentID=ta.AssignmentID), 0) AS HoursConducted
                FROM TeacherAssignments ta
                JOIN Teachers  t   ON ta.TeacherID  = t.TeacherID
                JOIN Subjects  sub ON ta.SubjectID  = sub.SubjectID
                JOIN Groups    g   ON ta.GroupID    = g.GroupID
                JOIN Semesters sem ON ta.SemesterID = sem.SemesterID
                {where}
                ORDER BY sem.IsCurrent DESC, sem.StartDate DESC, t.LastName, g.GroupName", conn);

            if (semesterId.HasValue) cmd.Parameters.AddWithValue("@SemID", semesterId.Value);
            if (teacherId.HasValue) cmd.Parameters.AddWithValue("@TchID", teacherId.Value);

            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new TeacherAssignment
                {
                    AssignmentID = (int)r["AssignmentID"],
                    TeacherID = (int)r["TeacherID"],
                    TeacherName = r["TName"].ToString()!,         // ← исправлено
                    SubjectName = r["SubjectName"].ToString()!,
                    HoursTotal = (int)(short)r["HoursTotal"],
                    GroupID = (int)r["GroupID"],
                    GroupName = r["GroupName"].ToString()!,
                    SemesterID = (int)r["SemesterID"],
                    SemesterName = r["SemesterName"].ToString()!,
                    HoursConducted = (int)r["HoursConducted"]
                });
            return list;
        }

        private TeacherAssignment? GetAssignmentByID(SqlConnection conn, int assignmentId)
        {
            using var cmd = new SqlCommand(@"
                SELECT ta.AssignmentID, ta.TeacherID,
                       t.LastName+' '+t.FirstName AS TName,
                       sub.SubjectName, sub.HoursTotal,
                       g.GroupID, g.GroupName,
                       sem.SemesterID, sem.SemesterName,
                       ISNULL((SELECT SUM(l.HoursCount) FROM Lessons l
                               WHERE l.AssignmentID=ta.AssignmentID), 0) AS HoursConducted
                FROM TeacherAssignments ta
                JOIN Teachers  t   ON ta.TeacherID  = t.TeacherID
                JOIN Subjects  sub ON ta.SubjectID  = sub.SubjectID
                JOIN Groups    g   ON ta.GroupID    = g.GroupID
                JOIN Semesters sem ON ta.SemesterID = sem.SemesterID
                WHERE ta.AssignmentID=@AID", conn);
            cmd.Parameters.AddWithValue("@AID", assignmentId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new TeacherAssignment
            {
                AssignmentID = (int)r["AssignmentID"],
                TeacherID = (int)r["TeacherID"],
                TeacherName = r["TName"].ToString()!,
                SubjectName = r["SubjectName"].ToString()!,
                HoursTotal = (int)(short)r["HoursTotal"],
                GroupID = (int)r["GroupID"],
                GroupName = r["GroupName"].ToString()!,
                SemesterID = (int)r["SemesterID"],
                SemesterName = r["SemesterName"].ToString()!,
                HoursConducted = (int)r["HoursConducted"]
            };
        }

        private List<Lesson> GetLessonsByAssignment(SqlConnection conn, int assignmentId)
        {
            var list = new List<Lesson>();
            using var cmd = new SqlCommand(@"
                SELECT LessonID,AssignmentID,LessonDate,LessonNumber,Topic,LessonType,HoursCount
                FROM Lessons WHERE AssignmentID=@AID ORDER BY LessonDate,LessonNumber", conn);
            cmd.Parameters.AddWithValue("@AID", assignmentId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new Lesson
                {
                    LessonID = (int)r["LessonID"],
                    AssignmentID = (int)r["AssignmentID"],
                    LessonDate = (DateTime)r["LessonDate"],
                    LessonNumber = (byte)r["LessonNumber"],
                    Topic = r["Topic"].ToString()!,
                    LessonType = r["LessonType"].ToString()!,
                    HoursCount = (byte)r["HoursCount"]
                });
            return list;
        }

        private List<Student> GetStudentsByGroup(SqlConnection conn, int groupId)
        {
            var list = new List<Student>();
            using var cmd = new SqlCommand(@"
                SELECT StudentID,LastName,FirstName,Patronymic,GroupID
                FROM Students WHERE GroupID=@GID AND IsActive=1 ORDER BY LastName,FirstName", conn);
            cmd.Parameters.AddWithValue("@GID", groupId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new Student
                {
                    StudentID = (int)r["StudentID"],
                    LastName = r["LastName"].ToString()!,
                    FirstName = r["FirstName"].ToString()!,
                    Patronymic = r["Patronymic"].ToString()!,
                    GroupID = (int)r["GroupID"]
                });
            return list;
        }
    }
}