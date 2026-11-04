/* Plain help instead of a raw error (ledger 0504).
 *
 * Every problem a student sees says two things in two short sentences: what happened, and
 * the one thing to do next. The host already writes most of its answers that way; what it
 * cannot always do is turn an exception from Windows, the network or the school site into
 * words, so a few of those reached the window as they were (the sign-in screen showed
 * error.Message, and an action that failed deep inside could answer with one). ArmoryHelp
 * reads a message and, when it is technical, answers with the plain card for what it
 * means. A message that is already plain passes through unchanged.
 *
 * The cases are the real ones in the Armory reports up to 2026-11-04: the school site
 * answering 521 (dozens of crashes on 2026-10-30), a cut-off answer (JsonReaderException),
 * slow or failed passes with nothing moving, a sign-in that ran out, a file SolidWorks
 * still holds, a folder that is not the student's, and a full disk.
 * tools/agent-ui/first-run-check.mjs holds every card to grade 5 and two sentences, and
 * holds each real report line to the card it should get. */
(function () {
	'use strict';
	var CARDS = [
		{ id: 'offline', test: /failed with 5\d\d|status code:? ?5\d\d|\b52[0-9]\b.*(?:failed|error)|timed? ?out|timeout|No such host|could not be resolved|SocketException|HttpRequestException|network|connection (?:was|is) (?:closed|reset|refused)|ECONN|ETIMEDOUT|A task was canceled|SSL connection|unreachable/i,
			what: 'Armory can’t reach the school site right now.', next: 'Keep working, and Armory sends your work when it can.' },
		{ id: 'cutOff', test: /JsonReaderException|JsonException|end of data|unexpected end|Unexpected token|invalid JSON/i,
			what: 'The school site sent back only part of an answer.', next: 'Wait a minute, and Armory will ask again.' },
		{ id: 'signIn', test: /\b401\b|JWT|token (?:has )?expired|refresh token|unauthori[sz]ed|invalid_grant|PGRST301/i,
			what: 'Your sign-in ran out.', next: 'Sign in again with your school Google account.' },
		{ id: 'inUse', test: /being used by another process|sharing violation|in use by another|ERROR_SHARING_VIOLATION|0x80070020/i,
			what: 'SolidWorks still has this file open.', next: 'Close it in SolidWorks, then try again.' },
		{ id: 'notYours', test: /not_your_folder|PT403|Access to the path .* is denied|UnauthorizedAccessException|permission denied/i,
			what: 'This folder is not yours to change.', next: 'Save in your own folder, or ask your teacher.' },
		{ id: 'notMember', test: /\b42501\b|not a (?:project )?member|not_member/i,
			what: 'You are not on this project.', next: 'Ask your teacher to add you by your school email.' },
		{ id: 'diskFull', test: /not enough space|disk (?:is )?full|ENOSPC|0x80070070/i,
			what: 'This computer is out of space.', next: 'Tell your teacher so they can make room.' }
	];
	var GENERIC = { id: 'generic', what: 'Something went wrong in Armory.', next: 'Your files are safe, so try again or click Report a problem.' };
	// What makes a message technical: names from code, codes, and machine punctuation.
	var RAW = /Exception\b|\bat [A-Z][\w.]+\(|failed with \d{3}|status code|\bHTTP\b|\bJSON\b|\bSQL\b|\bRPC\b|\barmory_[a-z_]+|\bPGRST\d+|\bPT\d{3}\b|\b(?:42501|23505|40P01|55006|P0001|P0002)\b|0x[0-9A-Fa-f]{6,}|\berrno\b|\bECONN|ETIMEDOUT|stack ?trace|Object reference|null reference|[{}\[\]<>|`]|LineNumber|BytePosition/;

	function isRaw(message) {
		return typeof message === 'string' && RAW.test(message);
	}
	/** The card for a message: a known case, the generic one for anything else technical, or
	 *  null for words that are already plain. */
	function card(message) {
		if (typeof message !== 'string' || !message.trim()) return null;
		for (var i = 0; i < CARDS.length; i++) if (CARDS[i].test.test(message)) return CARDS[i];
		return isRaw(message) ? GENERIC : null;
	}
	/** One plain line for a result or status: what happened, then what to do. */
	function say(message) {
		var c = card(message);
		return c ? c.what + ' ' + c.next : message;
	}
	window.ArmoryHelp = { cards: CARDS, generic: GENERIC, card: card, say: say, isRaw: isRaw };
})();
