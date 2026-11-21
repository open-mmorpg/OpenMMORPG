namespace MultiplayerARPG.MMO
{
    // The .NET half of the kit's DefaultLoginServerDataManager (Server/Login). The kit's half keeps
    // character ids and access tokens; these two read the character creation rules from
    // Config/characterCreationData.json instead of Unity's GameInstance.
    public partial class DefaultLoginServerDataManager
    {
        // Follows the Unity half's rules, with the exported data standing in for GameInstance.
        public bool CanCreateCharacter(ref int dataId, ref int entityId, ref int factionId, IList<CharacterDataBoolean> publicBools, IList<CharacterDataInt32> publicInts, IList<CharacterDataFloat32> publicFloats, out UITextKeys errorMessage)
        {
            errorMessage = UITextKeys.NONE;
            CharacterCreationData creationData = DataManager.CharacterCreationData;

            if (!creationData.AvailableCharacters.TryGetValue(entityId, out Dictionary<int, PlayerCharacterData> charactersOfEntity))
            {
                errorMessage = UITextKeys.UI_ERROR_INVALID_CHARACTER_ENTITY;
                return false;
            }

            if (!charactersOfEntity.ContainsKey(dataId))
            {
                errorMessage = UITextKeys.UI_ERROR_INVALID_CHARACTER_DATA;
                return false;
            }

            // AvailableFactionIds is the game's unlocked factions. A game without factions exports
            // none, and its characters have faction 0.
            List<int> factionIds = creationData.AvailableFactionIds;
            if (factionIds == null || factionIds.Count <= 0)
            {
                factionId = 0;
                return true;
            }

            // A random faction, if the player did not pick one that can be picked
            if (!factionIds.Contains(factionId))
                factionId = factionIds[GenericUtils.RandomInt(0, factionIds.Count)];

            return true;
        }

        public void SetNewPlayerCharacterData(PlayerCharacterData playerCharacterData, string characterName, int dataId, int entityId, int factionId, IList<CharacterDataBoolean> publicBools, IList<CharacterDataInt32> publicInts, IList<CharacterDataFloat32> publicFloats)
        {
            DataManager.CharacterCreationData.SetCreateCharacterData(playerCharacterData, playerCharacterData.Id, playerCharacterData.UserId, characterName, dataId, entityId, factionId, publicBools, publicInts, publicFloats);
        }
    }
}
