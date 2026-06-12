namespace GSCode.Parser.SPA.Models
{
    internal class ScriptApiJsonLibrary
    {
        /// <summary>
        /// The language ID this API is for (gsc/csc).
        /// </summary>
        public required string LanguageId { get; set; }

        /// <summary>
        /// The game this is for (t7).
        /// </summary>
        public required string GameId { get; set; }

        /// <summary>
        /// The version of this API.
        /// </summary>
        public required int Revision { get; set; }

        /// <summary>
        /// The API functions this result is loading into the SPA.
        /// </summary>
        public required List<ScrFunction> Api { get; set; }
    }
}
