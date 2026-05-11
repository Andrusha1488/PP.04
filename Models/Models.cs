namespace StudentDiaryWeb.Models
{
    // ================================================================
    //  СПРАВОЧНИКИ
    // ================================================================

    public class Teacher
    {
        public int TeacherID { get; set; }
        public int UserID { get; set; }
        public string LastName { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string Patronymic { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string FullName => $"{LastName} {FirstName} {Patronymic}".Trim();
        public string ShortName =>
            $"{LastName} {(FirstName.Length > 0 ? FirstName[0] + "." : "")}{(Patronymic.Length > 0 ? Patronymic[0] + "." : "")}".Trim();
    }

    public class Methodist
    {
        public int MethodistID { get; set; }
        public int UserID { get; set; }
        public string LastName { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string Patronymic { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string FullName => $"{LastName} {FirstName} {Patronymic}".Trim();
    }

    public class Group
    {
        public int GroupID { get; set; }
        public string GroupName { get; set; } = string.Empty;
        public int CourseYear { get; set; }
        public string Specialty { get; set; } = string.Empty;
    }

    public class Student
    {
        public int StudentID { get; set; }
        // UserID намеренно отсутствует — студенты не логинятся
        public string LastName { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string Patronymic { get; set; } = string.Empty;
        public int GroupID { get; set; }
        public string GroupName { get; set; } = string.Empty;
        public DateTime EnrollmentDate { get; set; }
        public bool IsActive { get; set; } = true;
        public string FullName => $"{LastName} {FirstName} {Patronymic}".Trim();
    }

    public class Subject
    {
        public int SubjectID { get; set; }
        public string SubjectName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int HoursTotal { get; set; }
    }

    public class Semester
    {
        public int SemesterID { get; set; }
        public string SemesterName { get; set; } = string.Empty;
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public int AcademicYear { get; set; }
        public bool IsCurrent { get; set; }
    }

    // ================================================================
    //  НАЗНАЧЕНИЕ — Преподаватель + Предмет + Группа + Семестр
    //  Это ключевая сущность: к ней привязаны занятия и итоговые оценки
    // ================================================================

    public class TeacherAssignment
    {
        public int AssignmentID { get; set; }
        public int TeacherID { get; set; }
        public string TeacherName { get; set; } = string.Empty;
        public int SubjectID { get; set; }
        public string SubjectName { get; set; } = string.Empty;
        public int GroupID { get; set; }
        public string GroupName { get; set; } = string.Empty;
        public int SemesterID { get; set; }
        public string SemesterName { get; set; } = string.Empty;
        public int HoursTotal { get; set; }        // из Subjects
        public int HoursConducted { get; set; }    // сколько фактически проведено
    }

    // ================================================================
    //  ЗАНЯТИЕ — один «столбец» бумажного журнала
    // ================================================================

    public class Lesson
    {
        public int LessonID { get; set; }
        public int AssignmentID { get; set; }

        // Данные назначения (денормализованы для удобства отображения)
        public string SubjectName { get; set; } = string.Empty;
        public string GroupName { get; set; } = string.Empty;
        public string TeacherName { get; set; } = string.Empty;

        public DateTime LessonDate { get; set; }
        public int LessonNumber { get; set; }       // номер пары
        public string Topic { get; set; } = string.Empty;

        // Тип: Лекция / Практика / Лабораторная / Зачёт / Контрольная
        public string LessonType { get; set; } = "Лекция";
        public int HoursCount { get; set; } = 2;

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    // ================================================================
    //  ЗАПИСЬ ЖУРНАЛА — «ячейка» (один студент на одном занятии)
    // ================================================================

    public class JournalEntry
    {
        public int EntryID { get; set; }
        public int LessonID { get; set; }
        public int StudentID { get; set; }
        public string StudentName { get; set; } = string.Empty;

        // Посещаемость: null = присутствовал, "н" = неуважительная, "у" = уважительная
        public string? Attendance { get; set; }

        // Оценка: null = не оценивался, "2"-"5", "зач", "незач"
        public string? Grade { get; set; }

        public string Comment { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; }

        // Вспомогательные свойства для отображения
        public bool IsAbsent => Attendance != null;
        public string DisplayCell
        {
            get
            {
                if (Attendance != null) return Attendance.ToUpper(); // Н или У
                if (Grade != null) return Grade;
                return "";
            }
        }
    }

    // ================================================================
    //  ЖУРНАЛ ГРУППЫ — главная вьюмодель страницы журнала
    //  Строки = студенты, Столбцы = занятия, Ячейки = JournalEntry
    // ================================================================

    public class JournalViewModel
    {
        public TeacherAssignment Assignment { get; set; } = new();
        public List<Student> Students { get; set; } = new();
        public List<Lesson> Lessons { get; set; } = new();

        // Словарь: [StudentID][LessonID] = JournalEntry
        public Dictionary<int, Dictionary<int, JournalEntry>> Entries { get; set; } = new();

        // Средний балл по студенту
        public Dictionary<int, decimal?> AverageGrades { get; set; } = new();

        // Количество пропусков по студенту
        public Dictionary<int, int> AbsenceCounts { get; set; } = new();

        // Замечания методиста (LessonID → список)
        public Dictionary<int, List<JournalComment>> Comments { get; set; } = new();

        // Последняя проверка методиста
        public JournalReview? LastReview { get; set; }
    }

    // ================================================================
    //  ИТОГОВЫЕ ОЦЕНКИ
    // ================================================================

    public class FinalGrade
    {
        public int FinalGradeID { get; set; }
        public int AssignmentID { get; set; }
        public int StudentID { get; set; }
        public string StudentName { get; set; } = string.Empty;
        public string GroupName { get; set; } = string.Empty;
        public string SubjectName { get; set; } = string.Empty;
        public string SemesterName { get; set; } = string.Empty;
        public string FinalGradeValue { get; set; } = string.Empty;  // "5","4","3","2","зач","незач"
        public string GradeForm { get; set; } = string.Empty;         // Экзамен / Зачёт / Диф.зачёт
        public string Comment { get; set; } = string.Empty;
        public bool IsLocked { get; set; }
        public DateTime GradeDate { get; set; }
        public decimal? AverageGrade { get; set; }  // автоподсказка из текущих оценок
    }

    // ================================================================
    //  ЗАМЕЧАНИЯ МЕТОДИСТА
    // ================================================================

    public class JournalComment
    {
        public int CommentID { get; set; }
        public int LessonID { get; set; }
        public int MethodistID { get; set; }
        public string MethodistName { get; set; } = string.Empty;
        public string CommentText { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    // ================================================================
    //  ПРОВЕРКА ЖУРНАЛА МЕТОДИСТОМ
    // ================================================================

    public class JournalReview
    {
        public int ReviewID { get; set; }
        public int AssignmentID { get; set; }
        public int MethodistID { get; set; }
        public string MethodistName { get; set; } = string.Empty;
        public DateTime ReviewDate { get; set; }
        public string Note { get; set; } = string.Empty;
    }

    // ================================================================
    //  АУДИТ ЛОГ
    // ================================================================

    public class AuditLog
    {
        public int LogID { get; set; }
        public int UserID { get; set; }
        public string UserLogin { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string TableName { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string IPAddress { get; set; } = string.Empty;
    }

    // ================================================================
    //  ВЬЮМОДЕЛИ ДЛЯ ОТЧЁТОВ
    // ================================================================

    // Отчёт: успеваемость группы
    public class GroupProgressReport
    {
        public TeacherAssignment Assignment { get; set; } = new();
        public List<StudentProgressRow> Rows { get; set; } = new();
        public int TotalLessons { get; set; }
        public int TotalHours { get; set; }
        public int PlannedHours { get; set; }
    }

    public class StudentProgressRow
    {
        public int StudentID { get; set; }
        public string StudentName { get; set; } = string.Empty;
        public decimal? AverageGrade { get; set; }
        public int AbsenceCount { get; set; }
        public int GradeCount { get; set; }
        public bool IsDebtor => AverageGrade.HasValue && AverageGrade < 3;
        public string AverageDisplay =>
            AverageGrade.HasValue ? AverageGrade.Value.ToString("0.0") : "—";
    }

    // Вьюмодель для дашборда преподавателя
    public class TeacherDashboard
    {
        public List<TeacherAssignment> TodayAssignments { get; set; } = new();
        public List<TeacherAssignment> AllAssignments { get; set; } = new();
        public int TotalLessonsThisSemester { get; set; }
        public int TotalGradesThisSemester { get; set; }
        public List<JournalReview> RecentReviews { get; set; } = new();
    }

    // Вьюмодель для дашборда методиста
    public class MethodistDashboard
    {
        public List<TeacherAssignment> AllAssignments { get; set; } = new();
        public List<TeacherAssignment> NotReviewedThisMonth { get; set; } = new();
        public List<TeacherAssignment> BehindSchedule { get; set; } = new();  // отстают > 10%
    }

    // ================================================================
    //  ВЬЮМОДЕЛИ АВТОРИЗАЦИИ (остаются теми же)
    // ================================================================

    public class LoginViewModel
    {
        public string Login { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }
    }

    public class TwoFactorViewModel
    {
        public string Login { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }
    }

    public class RegisterViewModel
    {
        public string Login { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string Patronymic { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public int RoleID { get; set; } = 3;
        public string? ErrorMessage { get; set; }
    }

    public class ChangePasswordViewModel
    {
        public string OldPassword { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }
        public string? SuccessMessage { get; set; }
    }
}