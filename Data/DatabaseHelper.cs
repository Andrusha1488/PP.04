using Microsoft.Data.SqlClient;

namespace StudentDiaryWeb.Data
{
    public static class DatabaseHelper
    {
        private static string? _connectionString;

        public static void SetConnectionString(string connectionString)
        {
            _connectionString = connectionString;
        }

        public static SqlConnection GetConnection()
        {
            if (string.IsNullOrEmpty(_connectionString))
                throw new Exception("Connection string is not initialized.");

            var connection = new SqlConnection(_connectionString);
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
            catch
            {
                return false;
            }
        }
    }
}