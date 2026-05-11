using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace StudentDiaryWeb.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        // Главная страница — перенаправляем по роли
        public IActionResult Index()
        {
            string role = User.FindFirst(ClaimTypes.Role)?.Value ?? "";

            return role switch
            {
                "Преподаватель" => RedirectToAction("Index", "Teacher"),
                "Методист" => RedirectToAction("Index", "Methodist"),
                "Администратор" => RedirectToAction("Index", "Admin"),
                _ => RedirectToAction("Login", "Account")
            };
        }

        public IActionResult Privacy() => View();

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error() => View();
    }
}