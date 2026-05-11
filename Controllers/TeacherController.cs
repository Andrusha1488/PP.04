using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using StudentDiaryWeb.Data;
using StudentDiaryWeb.Models;
using StudentDiaryWeb.Services;
using System.Security.Claims;

namespace StudentDiaryWeb.Controllers
{
    [Authorize(Roles = "Преподаватель")]
    public class TeacherController : Controller
    {
        private int TeacherID => int.Parse(User.FindFirst("ProfileID")?.Value ?? "0");
        private int CurrentUserID => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
        private string CurrentLogin => User.Identity?.Name ?? "";
        private string CurrentIP => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

        // ================================================================
        //  ГЛАВНАЯ — список назначений с фильтром по семестру
        // ================================================================

        public IActionResult Index(string? filterSemester)
        {
            var model = new TeacherDashboard();
            ViewBag.FilterSemester = filterSemester ?? "";
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                model.AllAssignments = GetAssignments(conn, TeacherID);

                using (var cmd = new SqlCommand(@"
                    SELECT COUNT(*) FROM Lessons l
                    JOIN TeacherAssignments ta ON l.AssignmentID=ta.AssignmentID
                    JOIN Semesters s ON ta.SemesterID=s.SemesterID
                    WHERE ta.TeacherID=@TID AND s.IsCurrent=1", conn))
                {
                    cmd.Parameters.AddWithValue("@TID", TeacherID);
                    model.TotalLessonsThisSemester = (int)cmd.ExecuteScalar();
                }

                using (var cmd = new SqlCommand(@"
                    SELECT COUNT(*) FROM JournalEntries je
                    JOIN Lessons l ON je.LessonID=l.LessonID
                    JOIN TeacherAssignments ta ON l.AssignmentID=ta.AssignmentID
                    JOIN Semesters s ON ta.SemesterID=s.SemesterID
                    WHERE ta.TeacherID=@TID AND s.IsCurrent=1 AND je.Grade IS NOT NULL", conn))
                {
                    cmd.Parameters.AddWithValue("@TID", TeacherID);
                    model.TotalGradesThisSemester = (int)cmd.ExecuteScalar();
                }

                using (var cmd = new SqlCommand(@"
                    SELECT TOP 5 jr.ReviewID, jr.AssignmentID, jr.ReviewDate,
                           jr.Note, m.LastName+' '+m.FirstName AS MethodistName
                    FROM JournalReviews jr
                    JOIN Methodists m ON jr.MethodistID=m.MethodistID
                    JOIN TeacherAssignments ta ON jr.AssignmentID=ta.AssignmentID
                    WHERE ta.TeacherID=@TID
                    ORDER BY jr.ReviewDate DESC", conn))
                {
                    cmd.Parameters.AddWithValue("@TID", TeacherID);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        model.RecentReviews.Add(new JournalReview
                        {
                            ReviewID = (int)r["ReviewID"],
                            AssignmentID = (int)r["AssignmentID"],
                            ReviewDate = (DateTime)r["ReviewDate"],
                            Note = r["Note"].ToString()!,
                            MethodistName = r["MethodistName"].ToString()!
                        });
                }
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return View(model);
        }

        // ================================================================
        //  ЖУРНАЛ
        // ================================================================

        public IActionResult Journal(int assignmentId, string? dateFrom, string? dateTo)
        {
            ViewBag.DateFrom = dateFrom ?? "";
            ViewBag.DateTo = dateTo ?? "";
            var model = new JournalViewModel();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                model.Assignment = GetAssignmentByID(conn, assignmentId, TeacherID);
                if (model.Assignment == null) { TempData["Error"] = "Журнал не найден."; return RedirectToAction("Index"); }

                model.Students = GetStudentsByGroup(conn, model.Assignment.GroupID);
                model.Lessons = GetLessonsByAssignment(conn, assignmentId);

                if (model.Lessons.Any() && model.Students.Any())
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

                foreach (var student in model.Students)
                {
                    int sid = student.StudentID;
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
                        model.LastReview = new JournalReview { ReviewDate = (DateTime)r["ReviewDate"], Note = r["Note"].ToString()!, MethodistName = r["MethodistName"].ToString()! };
                }
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return View(model);
        }

        // ================================================================
        //  ЗАНЯТИЯ с фильтром
        // ================================================================

        public IActionResult Lessons(int assignmentId, string? filterType, string? dateFrom, string? dateTo)
        {
            ViewBag.FilterType = filterType ?? "";
            ViewBag.DateFrom = dateFrom ?? "";
            ViewBag.DateTo = dateTo ?? "";
            var allLessons = new List<Lesson>();
            var filtered = new List<Lesson>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                var assignment = GetAssignmentByID(conn, assignmentId, TeacherID);
                if (assignment == null) { TempData["Error"] = "Назначение не найдено."; return RedirectToAction("Index"); }
                ViewBag.Assignment = assignment;

                allLessons = GetLessonsByAssignment(conn, assignmentId);
                ViewBag.AllLessons = allLessons;
                ViewBag.HoursConducted = allLessons.Sum(l => l.HoursCount);
                ViewBag.HoursPlanned = assignment.HoursTotal;

                filtered = allLessons;
                if (!string.IsNullOrWhiteSpace(filterType))
                    filtered = filtered.Where(l => l.LessonType == filterType).ToList();
                if (!string.IsNullOrWhiteSpace(dateFrom) && DateTime.TryParse(dateFrom, out var dtFrom))
                    filtered = filtered.Where(l => l.LessonDate >= dtFrom).ToList();
                if (!string.IsNullOrWhiteSpace(dateTo) && DateTime.TryParse(dateTo, out var dtTo))
                    filtered = filtered.Where(l => l.LessonDate <= dtTo).ToList();
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return View(filtered);
        }

        [HttpGet]
        public IActionResult CreateLesson(int assignmentId)
        {
            using var conn = DatabaseHelper.GetConnection();
            var assignment = GetAssignmentByID(conn, assignmentId, TeacherID);
            if (assignment == null) return RedirectToAction("Index");
            ViewBag.Assignment = assignment;
            ViewBag.LessonTypes = LessonTypes();
            return View(new Lesson { AssignmentID = assignmentId, LessonDate = DateTime.Today, LessonNumber = 1, HoursCount = 2 });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult CreateLesson(Lesson model)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                var assignment = GetAssignmentByID(conn, model.AssignmentID, TeacherID);
                if (assignment == null) return RedirectToAction("Index");

                using var cmd = new SqlCommand(@"
                    INSERT INTO Lessons (AssignmentID,LessonDate,LessonNumber,Topic,LessonType,HoursCount)
                    OUTPUT INSERTED.LessonID
                    VALUES (@AID,@Date,@Num,@Topic,@Type,@Hours)", conn);
                cmd.Parameters.AddWithValue("@AID", model.AssignmentID);
                cmd.Parameters.AddWithValue("@Date", model.LessonDate);
                cmd.Parameters.AddWithValue("@Num", model.LessonNumber);
                cmd.Parameters.AddWithValue("@Topic", model.Topic?.Trim() ?? "");
                cmd.Parameters.AddWithValue("@Type", model.LessonType);
                cmd.Parameters.AddWithValue("@Hours", model.HoursCount);
                int lessonID = (int)cmd.ExecuteScalar();

                var students = GetStudentsByGroup(conn, assignment.GroupID);
                foreach (var s in students)
                {
                    using var ins = new SqlCommand("INSERT INTO JournalEntries (LessonID,StudentID,Attendance,Grade,Comment) VALUES (@LID,@SID,NULL,NULL,'')", conn);
                    ins.Parameters.AddWithValue("@LID", lessonID);
                    ins.Parameters.AddWithValue("@SID", s.StudentID);
                    ins.ExecuteNonQuery();
                }

                AuditService.Log(CurrentUserID, CurrentLogin, "Создание", "Lessons", $"Добавлено занятие {model.LessonDate:dd.MM.yyyy} «{model.Topic}»", CurrentIP);
                TempData["Success"] = "Занятие добавлено.";
                return RedirectToAction("FillLesson", new { lessonId = lessonID });
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Index");
        }

        [HttpGet]
        public IActionResult EditLesson(int lessonId)
        {
            Lesson? model = null;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                model = GetLessonByID(conn, lessonId, TeacherID);
                if (model == null) return RedirectToAction("Index");
                ViewBag.LessonTypes = LessonTypes();
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult EditLesson(Lesson model)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                var existing = GetLessonByID(conn, model.LessonID, TeacherID);
                if (existing == null) return RedirectToAction("Index");
                using var cmd = new SqlCommand(@"
                    UPDATE Lessons SET LessonDate=@Date,LessonNumber=@Num,Topic=@Topic,
                    LessonType=@Type,HoursCount=@Hours,UpdatedAt=GETDATE() WHERE LessonID=@ID", conn);
                cmd.Parameters.AddWithValue("@Date", model.LessonDate);
                cmd.Parameters.AddWithValue("@Num", model.LessonNumber);
                cmd.Parameters.AddWithValue("@Topic", model.Topic?.Trim() ?? "");
                cmd.Parameters.AddWithValue("@Type", model.LessonType);
                cmd.Parameters.AddWithValue("@Hours", model.HoursCount);
                cmd.Parameters.AddWithValue("@ID", model.LessonID);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Изменение", "Lessons", $"Изменено занятие ID={model.LessonID}", CurrentIP);
                TempData["Success"] = "Занятие обновлено.";
                return RedirectToAction("Lessons", new { assignmentId = existing.AssignmentID });
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult DeleteLesson(int lessonId)
        {
            int assignmentId = 0;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                var lesson = GetLessonByID(conn, lessonId, TeacherID);
                if (lesson == null) return RedirectToAction("Index");
                assignmentId = lesson.AssignmentID;
                using var cmd = new SqlCommand("DELETE FROM Lessons WHERE LessonID=@ID", conn);
                cmd.Parameters.AddWithValue("@ID", lessonId);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Удаление", "Lessons", $"Удалено занятие ID={lessonId}", CurrentIP);
                TempData["Success"] = "Занятие удалено.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Lessons", new { assignmentId });
        }

        // ================================================================
        //  ЗАПОЛНЕНИЕ ЖУРНАЛА
        // ================================================================

        [HttpGet]
        public IActionResult FillLesson(int lessonId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                var lesson = GetLessonByID(conn, lessonId, TeacherID);
                if (lesson == null) return RedirectToAction("Index");
                var students = GetStudentsByGroup(conn, GetAssignmentByID(conn, lesson.AssignmentID, TeacherID)!.GroupID);
                var entries = new Dictionary<int, JournalEntry>();
                using (var cmd = new SqlCommand("SELECT EntryID,StudentID,Attendance,Grade,ISNULL(Comment,'') AS Comment FROM JournalEntries WHERE LessonID=@LID", conn))
                {
                    cmd.Parameters.AddWithValue("@LID", lessonId);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        int sid = (int)r["StudentID"];
                        entries[sid] = new JournalEntry
                        {
                            EntryID = (int)r["EntryID"],
                            StudentID = sid,
                            LessonID = lessonId,
                            Attendance = r["Attendance"] == DBNull.Value ? null : r["Attendance"].ToString()!.Trim(),
                            Grade = r["Grade"] == DBNull.Value ? null : r["Grade"].ToString()!.Trim(),
                            Comment = r["Comment"].ToString()!
                        };
                    }
                }
                ViewBag.Lesson = lesson;
                ViewBag.Students = students;
                ViewBag.Entries = entries;
                ViewBag.Grades = GradeValues();
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult FillLesson(int lessonId, List<int> studentIds, List<string?> grades, List<string?> attendances, List<string> comments)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                var lesson = GetLessonByID(conn, lessonId, TeacherID);
                if (lesson == null) return RedirectToAction("Index");
                for (int i = 0; i < studentIds.Count; i++)
                {
                    int sid = studentIds[i];
                    string? grade = string.IsNullOrWhiteSpace(grades[i]) ? null : grades[i]!.Trim();
                    string? attendance = string.IsNullOrWhiteSpace(attendances[i]) ? null : attendances[i]!.Trim();
                    string comment = comments.Count > i ? (comments[i] ?? "") : "";
                    if (attendance != null) grade = null;
                    using var cmd = new SqlCommand(@"
                        UPDATE JournalEntries SET Grade=@G,Attendance=@A,Comment=@C,UpdatedAt=GETDATE()
                        WHERE LessonID=@LID AND StudentID=@SID", conn);
                    cmd.Parameters.AddWithValue("@G", (object?)grade ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@A", (object?)attendance ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@C", comment);
                    cmd.Parameters.AddWithValue("@LID", lessonId);
                    cmd.Parameters.AddWithValue("@SID", sid);
                    cmd.ExecuteNonQuery();
                }
                AuditService.Log(CurrentUserID, CurrentLogin, "Заполнение журнала", "JournalEntries", $"Занятие ID={lessonId}", CurrentIP);
                TempData["Success"] = "Журнал сохранён.";
                return RedirectToAction("Journal", new { assignmentId = lesson.AssignmentID });
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult SaveEntry(int lessonId, int studentId, string? grade, string? attendance, string? comment)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                var lesson = GetLessonByID(conn, lessonId, TeacherID);
                if (lesson == null) return Json(new { success = false, message = "Нет доступа" });
                if (attendance != null) grade = null;
                string? g = string.IsNullOrWhiteSpace(grade) ? null : grade.Trim();
                string? a = string.IsNullOrWhiteSpace(attendance) ? null : attendance.Trim();
                using var cmd = new SqlCommand(@"
                    IF EXISTS (SELECT 1 FROM JournalEntries WHERE LessonID=@LID AND StudentID=@SID)
                        UPDATE JournalEntries SET Grade=@G,Attendance=@A,Comment=@C,UpdatedAt=GETDATE()
                        WHERE LessonID=@LID AND StudentID=@SID
                    ELSE
                        INSERT INTO JournalEntries (LessonID,StudentID,Grade,Attendance,Comment)
                        VALUES (@LID,@SID,@G,@A,@C)", conn);
                cmd.Parameters.AddWithValue("@LID", lessonId);
                cmd.Parameters.AddWithValue("@SID", studentId);
                cmd.Parameters.AddWithValue("@G", (object?)g ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@A", (object?)a ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@C", comment ?? "");
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Изменение ячейки", "JournalEntries", $"Занятие ID={lessonId}, студент ID={studentId}: оценка={g}, посещ={a}", CurrentIP);
                return Json(new { success = true });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        // ================================================================
        //  ИТОГОВЫЕ ОЦЕНКИ с фильтрами
        // ================================================================

        public IActionResult FinalGrades(int assignmentId, string? filterGrade, string? filterLocked)
        {
            ViewBag.FilterGrade = filterGrade ?? "";
            ViewBag.FilterLocked = filterLocked ?? "";
            ViewBag.Assignment = null;
            var list = new List<FinalGrade>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                var assignment = GetAssignmentByID(conn, assignmentId, TeacherID);
                if (assignment == null) return RedirectToAction("Index");
                ViewBag.Assignment = assignment;

                using var cmd = new SqlCommand(@"
                    SELECT fg.FinalGradeID, fg.StudentID,
                           s.LastName+' '+s.FirstName+' '+s.Patronymic AS StudentName,
                           fg.FinalGrade, fg.GradeForm,
                           ISNULL(fg.Comment,'') AS Comment, fg.IsLocked, fg.GradeDate
                    FROM FinalGrades fg JOIN Students s ON fg.StudentID=s.StudentID
                    WHERE fg.AssignmentID=@AID ORDER BY s.LastName,s.FirstName", conn);
                cmd.Parameters.AddWithValue("@AID", assignmentId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new FinalGrade
                    {
                        FinalGradeID = (int)r["FinalGradeID"],
                        StudentID = (int)r["StudentID"],
                        StudentName = r["StudentName"].ToString()!.Trim(),
                        FinalGradeValue = r["FinalGrade"].ToString()!,
                        GradeForm = r["GradeForm"].ToString()!,
                        Comment = r["Comment"].ToString()!,
                        IsLocked = (bool)r["IsLocked"],
                        GradeDate = (DateTime)r["GradeDate"]
                    });
                r.Close();

                foreach (var fg in list)
                {
                    using var avg = new SqlCommand(@"
                        SELECT AVG(CAST(je.Grade AS DECIMAL(4,2)))
                        FROM JournalEntries je JOIN Lessons l ON je.LessonID=l.LessonID
                        WHERE l.AssignmentID=@AID AND je.StudentID=@SID AND ISNUMERIC(je.Grade)=1", conn);
                    avg.Parameters.AddWithValue("@AID", assignmentId);
                    avg.Parameters.AddWithValue("@SID", fg.StudentID);
                    var res = avg.ExecuteScalar();
                    fg.AverageGrade = res == DBNull.Value || res == null ? null : Math.Round((decimal)res, 1);
                }
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return View(list);
        }

        [HttpGet]
        public IActionResult CreateFinalGrades(int assignmentId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                var assignment = GetAssignmentByID(conn, assignmentId, TeacherID);
                if (assignment == null) return RedirectToAction("Index");
                ViewBag.Assignment = assignment;
                ViewBag.GradeForms = GradeForms();
                ViewBag.GradeValues = GradeValues();
                var students = GetStudentsByGroup(conn, assignment.GroupID);
                var rows = new List<FinalGrade>();
                foreach (var s in students)
                {
                    using var chk = new SqlCommand("SELECT FinalGrade FROM FinalGrades WHERE AssignmentID=@AID AND StudentID=@SID", conn);
                    chk.Parameters.AddWithValue("@AID", assignmentId);
                    chk.Parameters.AddWithValue("@SID", s.StudentID);
                    string? existing = chk.ExecuteScalar()?.ToString();
                    using var avg = new SqlCommand(@"
                        SELECT AVG(CAST(je.Grade AS DECIMAL(4,2)))
                        FROM JournalEntries je JOIN Lessons l ON je.LessonID=l.LessonID
                        WHERE l.AssignmentID=@AID AND je.StudentID=@SID AND ISNUMERIC(je.Grade)=1", conn);
                    avg.Parameters.AddWithValue("@AID", assignmentId);
                    avg.Parameters.AddWithValue("@SID", s.StudentID);
                    var avgRes = avg.ExecuteScalar();
                    rows.Add(new FinalGrade
                    {
                        AssignmentID = assignmentId,
                        StudentID = s.StudentID,
                        StudentName = s.FullName,
                        FinalGradeValue = existing ?? "",
                        AverageGrade = avgRes == DBNull.Value || avgRes == null ? null : Math.Round((decimal)avgRes, 1)
                    });
                }
                return View(rows);
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("Index");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult CreateFinalGrades(int assignmentId, List<int> studentIds, List<string> finalGrades, string gradeForm, List<string> comments)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                var assignment = GetAssignmentByID(conn, assignmentId, TeacherID);
                if (assignment == null) return RedirectToAction("Index");
                for (int i = 0; i < studentIds.Count; i++)
                {
                    int sid = studentIds[i];
                    string grade = finalGrades.Count > i ? finalGrades[i] : "";
                    string com = comments.Count > i ? comments[i] : "";
                    if (string.IsNullOrWhiteSpace(grade)) continue;
                    using var cmd = new SqlCommand(@"
                        IF EXISTS (SELECT 1 FROM FinalGrades WHERE AssignmentID=@AID AND StudentID=@SID)
                            UPDATE FinalGrades SET FinalGrade=@G,GradeForm=@F,Comment=@C,GradeDate=GETDATE()
                            WHERE AssignmentID=@AID AND StudentID=@SID
                        ELSE
                            INSERT INTO FinalGrades (AssignmentID,StudentID,FinalGrade,GradeForm,Comment,GradeDate)
                            VALUES (@AID,@SID,@G,@F,@C,GETDATE())", conn);
                    cmd.Parameters.AddWithValue("@AID", assignmentId);
                    cmd.Parameters.AddWithValue("@SID", sid);
                    cmd.Parameters.AddWithValue("@G", grade.Trim());
                    cmd.Parameters.AddWithValue("@F", gradeForm);
                    cmd.Parameters.AddWithValue("@C", com ?? "");
                    cmd.ExecuteNonQuery();
                }
                AuditService.Log(CurrentUserID, CurrentLogin, "Итоговые оценки", "FinalGrades", $"Назначение ID={assignmentId}", CurrentIP);
                TempData["Success"] = "Итоговые оценки сохранены.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("FinalGrades", new { assignmentId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult DeleteFinalGrade(int finalGradeId, int assignmentId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = new SqlCommand(@"
                    DELETE fg FROM FinalGrades fg
                    JOIN TeacherAssignments ta ON fg.AssignmentID=ta.AssignmentID
                    WHERE fg.FinalGradeID=@ID AND ta.TeacherID=@TID AND fg.IsLocked=0", conn);
                cmd.Parameters.AddWithValue("@ID", finalGradeId);
                cmd.Parameters.AddWithValue("@TID", TeacherID);
                cmd.ExecuteNonQuery();
                AuditService.Log(CurrentUserID, CurrentLogin, "Удаление", "FinalGrades", $"Удалена итоговая оценка ID={finalGradeId}", CurrentIP);
                TempData["Success"] = "Итоговая оценка удалена.";
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return RedirectToAction("FinalGrades", new { assignmentId });
        }

        // ================================================================
        //  ОТЧЁТ с фильтрами
        // ================================================================

        public IActionResult Report(int assignmentId, string? filterStatus, string? minAvg, string? maxAbsences)
        {
            ViewBag.FilterStatus = filterStatus ?? "";
            ViewBag.MinAvg = minAvg ?? "";
            ViewBag.MaxAbsences = maxAbsences ?? "";
            var model = new GroupProgressReport();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                var assignment = GetAssignmentByID(conn, assignmentId, TeacherID);
                if (assignment == null) return RedirectToAction("Index");
                model.Assignment = assignment;
                var lessons = GetLessonsByAssignment(conn, assignmentId);
                model.TotalLessons = lessons.Count;
                model.TotalHours = lessons.Sum(l => l.HoursCount);
                model.PlannedHours = assignment.HoursTotal;

                foreach (var s in GetStudentsByGroup(conn, assignment.GroupID))
                {
                    using var cmd = new SqlCommand(@"
                        SELECT COUNT(CASE WHEN je.Attendance IS NOT NULL THEN 1 END) AS Absences,
                               COUNT(CASE WHEN je.Grade IS NOT NULL THEN 1 END)      AS GradeCount,
                               AVG(CASE WHEN ISNUMERIC(je.Grade)=1 THEN CAST(je.Grade AS DECIMAL(4,2)) ELSE NULL END) AS Avg
                        FROM JournalEntries je JOIN Lessons l ON je.LessonID=l.LessonID
                        WHERE l.AssignmentID=@AID AND je.StudentID=@SID", conn);
                    cmd.Parameters.AddWithValue("@AID", assignmentId);
                    cmd.Parameters.AddWithValue("@SID", s.StudentID);
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                        model.Rows.Add(new StudentProgressRow
                        {
                            StudentID = s.StudentID,
                            StudentName = s.FullName,
                            AbsenceCount = (int)r["Absences"],
                            GradeCount = (int)r["GradeCount"],
                            AverageGrade = r["Avg"] == DBNull.Value ? null : Math.Round((decimal)r["Avg"], 1)
                        });
                }
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }
            return View(model);
        }

        // ================================================================
        //  ВСПОМОГАТЕЛЬНЫЕ
        // ================================================================

        private List<TeacherAssignment> GetAssignments(SqlConnection conn, int teacherID)
        {
            var list = new List<TeacherAssignment>();
            using var cmd = new SqlCommand(@"
                SELECT ta.AssignmentID, ta.TeacherID, sub.SubjectName, sub.HoursTotal,
                       g.GroupID, g.GroupName, sem.SemesterID, sem.SemesterName, sem.IsCurrent,
                       ISNULL((SELECT SUM(l2.HoursCount) FROM Lessons l2 WHERE l2.AssignmentID=ta.AssignmentID),0) AS HoursConducted
                FROM TeacherAssignments ta
                JOIN Subjects  sub ON ta.SubjectID  = sub.SubjectID
                JOIN Groups    g   ON ta.GroupID    = g.GroupID
                JOIN Semesters sem ON ta.SemesterID = sem.SemesterID
                WHERE ta.TeacherID=@TID
                ORDER BY sem.IsCurrent DESC, sem.StartDate DESC, g.GroupName, sub.SubjectName", conn);
            cmd.Parameters.AddWithValue("@TID", teacherID);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new TeacherAssignment
                {
                    AssignmentID = (int)r["AssignmentID"],
                    TeacherID = (int)r["TeacherID"],
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

        private TeacherAssignment? GetAssignmentByID(SqlConnection conn, int assignmentId, int teacherID)
        {
            using var cmd = new SqlCommand(@"
                SELECT ta.AssignmentID, ta.TeacherID, sub.SubjectName, sub.HoursTotal,
                       g.GroupID, g.GroupName, sem.SemesterID, sem.SemesterName,
                       ISNULL((SELECT SUM(l2.HoursCount) FROM Lessons l2 WHERE l2.AssignmentID=ta.AssignmentID),0) AS HoursConducted
                FROM TeacherAssignments ta
                JOIN Subjects  sub ON ta.SubjectID  = sub.SubjectID
                JOIN Groups    g   ON ta.GroupID    = g.GroupID
                JOIN Semesters sem ON ta.SemesterID = sem.SemesterID
                WHERE ta.AssignmentID=@AID AND ta.TeacherID=@TID", conn);
            cmd.Parameters.AddWithValue("@AID", assignmentId);
            cmd.Parameters.AddWithValue("@TID", teacherID);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new TeacherAssignment
            {
                AssignmentID = (int)r["AssignmentID"],
                TeacherID = (int)r["TeacherID"],
                SubjectName = r["SubjectName"].ToString()!,
                HoursTotal = (int)(short)r["HoursTotal"],
                GroupID = (int)r["GroupID"],
                GroupName = r["GroupName"].ToString()!,
                SemesterID = (int)r["SemesterID"],
                SemesterName = r["SemesterName"].ToString()!,
                HoursConducted = (int)r["HoursConducted"]
            };
        }

        private Lesson? GetLessonByID(SqlConnection conn, int lessonId, int teacherID)
        {
            using var cmd = new SqlCommand(@"
                SELECT l.LessonID, l.AssignmentID, l.LessonDate, l.LessonNumber,
                       l.Topic, l.LessonType, l.HoursCount, sub.SubjectName, g.GroupName
                FROM Lessons l
                JOIN TeacherAssignments ta ON l.AssignmentID=ta.AssignmentID
                JOIN Subjects sub ON ta.SubjectID=sub.SubjectID
                JOIN Groups   g   ON ta.GroupID=g.GroupID
                WHERE l.LessonID=@LID AND ta.TeacherID=@TID", conn);
            cmd.Parameters.AddWithValue("@LID", lessonId);
            cmd.Parameters.AddWithValue("@TID", teacherID);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new Lesson
            {
                LessonID = (int)r["LessonID"],
                AssignmentID = (int)r["AssignmentID"],
                LessonDate = (DateTime)r["LessonDate"],
                LessonNumber = (byte)r["LessonNumber"],
                Topic = r["Topic"].ToString()!,
                LessonType = r["LessonType"].ToString()!,
                HoursCount = (byte)r["HoursCount"],
                SubjectName = r["SubjectName"].ToString()!,
                GroupName = r["GroupName"].ToString()!
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

        private static List<string> GradeValues() => new() { "5", "4", "3", "2", "зач", "незач" };
        private static List<string> GradeForms() => new() { "Экзамен", "Зачёт", "Дифференцированный зачёт" };
        private static List<string> LessonTypes() => new() { "Лекция", "Практика", "Лабораторная работа", "Зачёт", "Контрольная работа" };
    }
}