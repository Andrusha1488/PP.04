using System.Collections.Concurrent;

namespace StudentDiaryWeb.Services
{
    // Хранит коды 2FA в памяти.
    // Ключ — логин пользователя, значение — код и время истечения.
    public class TwoFactorService
    {
        private readonly ConcurrentDictionary<string, (string Code, DateTime Expires)>
            _codes = new();

        private const int CodeLifetimeMinutes = 5;

        // Генерировать новый 6-значный код для пользователя
        public string GenerateCode(string login)
        {
            var code = new Random().Next(100000, 999999).ToString();
            _codes[login] = (code, DateTime.UtcNow.AddMinutes(CodeLifetimeMinutes));
            return code;
        }

        // Проверить введённый код
        public bool VerifyCode(string login, string code)
        {
            if (!_codes.TryGetValue(login, out var entry))
                return false;

            // Проверяем срок действия
            if (DateTime.UtcNow > entry.Expires)
            {
                _codes.TryRemove(login, out _);
                return false;
            }

            // Проверяем совпадение
            bool valid = entry.Code == code.Trim();
            if (valid)
                _codes.TryRemove(login, out _);

            return valid;
        }

        // Проверить — ожидает ли пользователь ввода кода
        public bool IsAwaitingCode(string login) =>
            _codes.TryGetValue(login, out var entry) &&
            DateTime.UtcNow <= entry.Expires;

        // Получить оставшееся время действия кода в секундах
        public int GetRemainingSeconds(string login)
        {
            if (!_codes.TryGetValue(login, out var entry)) return 0;
            var remaining = (entry.Expires - DateTime.UtcNow).TotalSeconds;
            return remaining > 0 ? (int)remaining : 0;
        }

        // Удалить код (при выходе или отмене)
        public void RemoveCode(string login) =>
            _codes.TryRemove(login, out _);
    }
}
