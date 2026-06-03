namespace FontStash.NET
{
	internal static class UtfDecoder
	{

		internal const int UTF8_ACCEPT = 0;
		internal const int UTF8_REJECT = 12;

		/// <summary>
		/// Decode a UTF-16 code unit sequence into Unicode codepoints.
		///
		/// The original C FontStash operates on UTF-8 byte arrays and uses a DFA state machine
		/// to reassemble multi-byte sequences. In C#, strings are UTF-16 — each str[i] is
		/// already a Unicode code unit. The UTF-8 DFA is replaced with surrogate pair handling.
		///
		/// Callers iterate str[i] and check: if DecodeCodepoint() != UTF8_ACCEPT, continue (skip).
		///
		/// BMP characters (U+0000-U+FFFF): single char → immediate ACCEPT with codepoint.
		/// Surrogate pairs (U+10000+, e.g. emoji): high surrogate → store + non-ACCEPT (skip),
		///   low surrogate → combine into full codepoint + ACCEPT.
		/// </summary>
		public static uint DecodeCodepoint(ref uint state, ref uint codep, uint charValue)
		{
			// High surrogate (first half of a surrogate pair for U+10000+)
			if (charValue >= 0xD800 && charValue <= 0xDBFF) {
				codep = charValue;  // store for next call
				state = 1;          // non-ACCEPT: caller continues to next char
				return state;
			}

			// Low surrogate (second half) — combine with stored high surrogate
			if (charValue >= 0xDC00 && charValue <= 0xDFFF && state == 1) {
				codep = (uint)char.ConvertToUtf32((char)codep, (char)charValue);
				state = UTF8_ACCEPT;
				return state;
			}

			// BMP character — direct codepoint
			codep = charValue;
			state = UTF8_ACCEPT;
			return state;
		}

		/// <summary>
		/// Legacy name alias for backwards compatibility within the codebase.
		/// </summary>
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
		public static uint DecUtf8(ref uint state, ref uint codep, uint charValue)
			=> DecodeCodepoint(ref state, ref codep, charValue);

	}
}