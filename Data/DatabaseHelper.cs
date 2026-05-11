using Microsoft.Data.SqlClient;

namespace StudentDiaryWeb.Data
{
    public static class DatabaseHelper
    {
        private static readonly string ConnectionString =
            @"Server=DESKTOP-HPUN3GH\SQLEXPRESS;Database=TeacherJournal;Integrated Security=True;TrustServerCertificate=True;";

        public static SqlConnection GetConnection()
        {
            var connection = new SqlConnection(ConnectionString);
            connection.Open();
            return connection;
        }

        public static bool TestConnection()
        {
            try
            {
                using var conn = GetConnection();
                return conn.State == System.Data.ConnectionState.Open;
            }
            catch { return false; }
        }
    }
}