using Microsoft.Data.SqlClient;
using StudentDiaryWeb.Data;

namespace StudentDiaryWeb.Services
{
    public class AuditService
    {
        // Проверяем что таблица AuditLog существует (создаём если нет)
        public static void EnsureAuditTableExists()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                string sql = @"
                    IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AuditLog')
                    CREATE TABLE AuditLog (
                        LogID     INT            NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        UserID    INT            NOT NULL DEFAULT 0,
                        UserLogin NVARCHAR(100)  NOT NULL DEFAULT '',
                        Action    NVARCHAR(100)  NOT NULL,
                        TableName NVARCHAR(100)  NOT NULL DEFAULT '',
                        Details   NVARCHAR(1000) NOT NULL DEFAULT '',
                        IPAddress NVARCHAR(50)   NOT NULL DEFAULT '',
                        CreatedAt DATETIME       NOT NULL DEFAULT GETDATE()
                    )";
                using var cmd = new SqlCommand(sql, conn);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        // Записать действие в журнал
        public static void Log(int userID, string userLogin, string action,
                               string tableName, string details, string ipAddress = "")
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                string sql = @"
                    INSERT INTO AuditLog
                        (UserID, UserLogin, Action, TableName, Details, IPAddress)
                    VALUES
                        (@UserID, @Login, @Action, @Table, @Details, @IP)";
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@UserID", userID);
                cmd.Parameters.AddWithValue("@Login", userLogin);
                cmd.Parameters.AddWithValue("@Action", action);
                cmd.Parameters.AddWithValue("@Table", tableName);
                cmd.Parameters.AddWithValue("@Details", details);
                cmd.Parameters.AddWithValue("@IP", ipAddress);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }
}