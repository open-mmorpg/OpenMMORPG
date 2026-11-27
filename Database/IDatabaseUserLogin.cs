namespace MultiplayerARPG.MMO
{
    /// <summary>
    /// Hashes and checks passwords. A database server calls <see cref="GetHashedPassword"/>,
    /// <see cref="VerifyPassword"/> and <see cref="NeedsRehash"/> from worker threads, several
    /// at once, so that a slow hash does not hold up its loop: keep them free of shared state.
    /// </summary>
    public interface IDatabaseUserLogin
    {
        string GetHashedPassword(string password);
        bool VerifyPassword(string password, string hashedPassword);
        /// <summary>True if the stored hash uses an outdated scheme and should be replaced after a successful login.</summary>
        bool NeedsRehash(string hashedPassword) => false;
        string GenerateNewId();
    }
}
