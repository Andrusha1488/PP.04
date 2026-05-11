// Models/AppUser.cs
namespace StudentDiaryWeb.Models
{
    public class AppUser
    {
        public int UserID { get; set; }
        public string Login { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public int RoleID { get; set; }
        public string RoleName { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }
}