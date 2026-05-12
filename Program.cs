using Microsoft.AspNetCore.Authentication.Cookies;
using StudentDiaryWeb.Services;
using StudentDiaryWeb.Data;

var builder = WebApplication.CreateBuilder(args);

DatabaseHelper.SetConnectionString(
    builder.Configuration.GetConnectionString("DefaultConnection")!);

// Добавляем MVC (контроллеры + представления)
builder.Services.AddControllersWithViews();

builder.Services.AddSingleton<TwoFactorService>();
builder.Services.AddScoped<EmailService>();
// Регистрируем наш сервис защиты от брутфорса как Singleton —
// один экземпляр на всё приложение, хранит счётчики попыток входа
builder.Services.AddSingleton<LoginProtectionService>();

// Настройка авторизации через куки
// Куки — это небольшой файл в браузере пользователя,
// который подтверждает что он уже вошёл в систему
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";        // куда редиректить если не вошёл
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/AccessDenied"; // если нет прав
        options.ExpireTimeSpan = TimeSpan.FromHours(8);     // сессия на 8 часов
        options.SlidingExpiration = true;            // продлевать при активности
        options.Cookie.HttpOnly = true;              // защита от XSS — JS не может читать куки
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = SameSiteMode.Strict; // защита от CSRF
    });

// Защита от CSRF атак — автоматически добавляет токен в формы
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
});

// Сессии для хранения временных данных
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.AddHttpContextAccessor();

// Убираем EventLog провайдер, который не работает на Somee
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var app = builder.Build();

// Создаём таблицу журнала аудита если её нет
AuditService.EnsureAuditTableExists();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();  // HTTPS обязателен в продакшне
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

// Защитные заголовки — защита от кликджекинга, XSS и других атак
app.Use(async (context, next) =>
{
    // Запрет на встраивание сайта в iframe — защита от кликджекинга
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    // Защита от XSS в браузере
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
    // Запрет угадывания типа контента
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    // Политика безопасности контента
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    await next();
});

app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

// Маршруты — по умолчанию открываем страницу входа
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}");

app.Run();