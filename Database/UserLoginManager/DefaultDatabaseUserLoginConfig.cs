namespace MultiplayerARPG.MMO
{
    [System.Serializable]
    public struct DefaultDatabaseUserLoginConfig
    {
        public string PasswordSaltPrefix { get; set; }
        public string PasswordSaltPostfix { get; set; }
        /// <summary>Rounds of the password hash. Zero means `DefaultDatabaseUserLogin.DefaultIterations`.</summary>
        public int PasswordHashIterations { get; set; }
    }
}
