using System.Collections.Concurrent;

namespace StudentDiaryWeb.Services
{
    // Защита от брутфорса — блокируем IP после 5 неудачных попыток входа.
    // ConcurrentDictionary безопасен при одновременных запросах от разных пользователей.
    public class LoginProtectionService
    {
        // Словарь: IP-адрес → (количество попыток, время последней попытки)
        private static readonly ConcurrentDictionary<string, (int Attempts, DateTime LastAttempt)>
            _failedAttempts = new();

        private const int MaxAttempts = 5;           // максимум попыток
        private const int BlockMinutes = 15;         // блокировка на 15 минут

        // Проверить — заблокирован ли этот IP
        public bool IsBlocked(string ipAddress)
        {
            if (!_failedAttempts.TryGetValue(ipAddress, out var info))
                return false;

            // Если прошло больше BlockMinutes — снимаем блокировку
            if (DateTime.UtcNow - info.LastAttempt > TimeSpan.FromMinutes(BlockMinutes))
            {
                _failedAttempts.TryRemove(ipAddress, out _);
                return false;
            }

            return info.Attempts >= MaxAttempts;
        }

        // Записать неудачную попытку входа
        public void RegisterFailedAttempt(string ipAddress)
        {
            _failedAttempts.AddOrUpdate(
                ipAddress,
                (1, DateTime.UtcNow),
                (_, old) => (old.Attempts + 1, DateTime.UtcNow));
        }

        // Сброс счётчика после успешного входа
        public void ResetAttempts(string ipAddress)
        {
            _failedAttempts.TryRemove(ipAddress, out _);
        }

        // Сколько минут осталось до снятия блокировки
        public int GetRemainingBlockMinutes(string ipAddress)
        {
            if (!_failedAttempts.TryGetValue(ipAddress, out var info))
                return 0;

            var remaining = BlockMinutes - (DateTime.UtcNow - info.LastAttempt).TotalMinutes;
            return remaining > 0 ? (int)Math.Ceiling(remaining) : 0;
        }
    }
}