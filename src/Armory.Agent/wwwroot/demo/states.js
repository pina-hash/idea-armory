/*
 * Demo states for the Armory Agent window.
 *
 * Used only outside WebView2 (a plain browser, the screenshot tools). bridge.js loads
 * this file when window.chrome.webview is missing and answers the page from it, so the
 * page never knows the difference. Every view below is a complete AgentView as
 * docs/agent/BRIDGE.md defines it; every detail is a complete FileDetail.
 *
 * The clock is fixed (NOW) so relative times ("5 minutes ago") and screenshots are
 * stable. The people, projects and files are FRC Team 5669 and an IDEA class at Bosco
 * Tech; the people are made up.
 *
 * Each state says which screens show it best (`screens`) and, when File detail
 * matters, which file to open (`detailFileId`). tools/agent-ui reads both to decide
 * what to render.
 */
(function () {
	'use strict';

	var NOW = '2026-10-01T15:30:00-07:00';
	var NOW_MS = Date.parse(NOW);
	var MIN = 1;
	var HOUR = 60;
	var DAY = 24 * 60;

	/** ISO-8601 time `minutes` before the demo clock. */
	function ago(minutes) {
		return new Date(NOW_MS - minutes * 60000).toISOString();
	}

	// People. "Me" is the student signed in on this computer.
	var ME = { name: 'Jordan Reyes', email: 'jordan.reyes@boscotech.edu', device: 'LAB-PC-14' };
	var MARIA = { name: 'Maria Lopez', email: 'maria.lopez@boscotech.edu', device: 'LAB-PC-07' };
	var ALEX = { name: 'Alex Kim', email: 'alex.kim@boscotech.edu', device: 'ALEX-LAPTOP' };
	var JORDAN = ME;
	var PINA = { name: 'Mr. Pina', email: 'pina@boscotech.edu', device: 'ROOM-209-PC' };

	function holder(person, sinceMinutes, savedToArmory, onMyOtherComputer) {
		var isMe = person === ME;
		return {
			name: person.name,
			email: person.email,
			device: onMyOtherComputer ? 'JORDAN-LAPTOP' : person.device,
			since: ago(sinceMinutes),
			isMe: isMe,
			isMyOtherComputer: !!onMyOtherComputer,
			savedToArmory: savedToArmory !== false
		};
	}

	var PROJECTS = [
		{ id: 'proj-robot-2027', name: 'Robot 2027', folders: ['', 'Drivetrain', 'Intake', 'Elevator', 'COTS'] },
		{ id: 'proj-idea-209', name: 'IDEA 209 Bridge', folders: ['', 'Trusses', 'Drawings'] }
	];

	// Every file on the server: id, project, folder, name, last saved by, minutes ago, size.
	var CATALOG = [
		['f-robot-asm', 'proj-robot-2027', '', 'Robot-2027.SLDASM', ALEX, 2 * DAY, 3870210],
		['f-gearbox', 'proj-robot-2027', 'Drivetrain', 'Gearbox.SLDASM', JORDAN, 20 * MIN, 1482113],
		['f-plate-left', 'proj-robot-2027', 'Drivetrain', 'Plate-Left.SLDPRT', MARIA, 1 * HOUR, 612480],
		['f-plate-right', 'proj-robot-2027', 'Drivetrain', 'Plate-Right.SLDPRT', MARIA, 70 * MIN, 609312],
		['f-wheel-hub', 'proj-robot-2027', 'Drivetrain', 'Wheel-Hub.SLDPRT', ALEX, 3 * HOUR, 284766],
		['f-dt-drawing', 'proj-robot-2027', 'Drivetrain', 'Drawing.SLDDRW', JORDAN, 1 * DAY, 951300],
		['f-intake-asm', 'proj-robot-2027', 'Intake', 'Intake.SLDASM', ALEX, 5 * HOUR, 2210004],
		['f-bracket', 'proj-robot-2027', 'Intake', 'Bracket.SLDPRT', ALEX, 4 * DAY, 198442],
		['f-roller-shaft', 'proj-robot-2027', 'Intake', 'Roller-Shaft.SLDPRT', MARIA, 2 * DAY, 120931],
		['f-elevator-asm', 'proj-robot-2027', 'Elevator', 'Elevator.SLDASM', MARIA, 6 * HOUR, 2650177],
		['f-carriage', 'proj-robot-2027', 'Elevator', 'Carriage-Plate.SLDPRT', JORDAN, 2 * DAY, 455030],
		['f-stage-tube', 'proj-robot-2027', 'Elevator', 'Stage-1-Tube.SLDPRT', ALEX, 3 * DAY, 141876],
		['f-hex-bearing', 'proj-robot-2027', 'COTS', 'Hex-Bearing-0.5in.SLDPRT', PINA, 20 * DAY, 88213],
		['f-motor', 'proj-robot-2027', 'COTS', 'Motor-Mount.SLDPRT', PINA, 20 * DAY, 301455],
		['f-collar', 'proj-robot-2027', 'COTS', 'Shaft-Collar.SLDPRT', PINA, 21 * DAY, 64220],
		['f-bridge-asm', 'proj-idea-209', '', 'Bridge.SLDASM', JORDAN, 3 * DAY, 1310442],
		['f-truss-side', 'proj-idea-209', 'Trusses', 'Truss-Side.SLDPRT', JORDAN, 3 * DAY, 402118],
		['f-gusset', 'proj-idea-209', 'Trusses', 'Gusset-Plate.SLDPRT', MARIA, 4 * DAY, 99876],
		['f-cross-brace', 'proj-idea-209', 'Trusses', 'Cross-Brace.SLDPRT', ALEX, 4 * DAY, 87021],
		['f-bridge-drawing', 'proj-idea-209', 'Drawings', 'Bridge-Drawing.SLDDRW', JORDAN, 5 * DAY, 733904]
	];

	function catalogEntry(fileId) {
		for (var i = 0; i < CATALOG.length; i++) if (CATALOG[i][0] === fileId) return CATALOG[i];
		throw new Error('No demo file ' + fileId);
	}
	function projectOf(projectId) {
		for (var i = 0; i < PROJECTS.length; i++) if (PROJECTS[i].id === projectId) return PROJECTS[i];
		throw new Error('No demo project ' + projectId);
	}
	function pathOf(entry) {
		var project = projectOf(entry[1]);
		return project.name + '/' + (entry[2] ? entry[2] + '/' : '') + entry[3];
	}

	/** The FileRow for one catalog entry, with this state's changes applied. */
	function fileRow(entry, change) {
		var row = {
			fileId: entry[0],
			name: entry[3],
			path: pathOf(entry),
			status: 'synced',
			holder: null,
			releaseNotChecked: false,
			updatedAt: ago(entry[5]),
			updatedBy: entry[4].name
		};
		if (change) for (var k in change) row[k] = change[k];
		return row;
	}

	/** Projects and folders as the server sees them, with per-file changes. */
	function projects(changes) {
		changes = changes || {};
		return PROJECTS.map(function (p) {
			return {
				id: p.id,
				name: p.name,
				folders: p.folders.map(function (folder) {
					return {
						path: folder,
						name: folder,
						files: CATALOG.filter(function (e) {
							return e[1] === p.id && e[2] === folder;
						}).map(function (e) {
							return fileRow(e, changes[e[0]]);
						})
					};
				})
			};
		});
	}

	/** A plain history: the current save, then older saves by the team. */
	function history(fileId, extra) {
		var e = catalogEntry(fileId);
		var authors = [e[4], MARIA, ALEX, JORDAN, ALEX];
		var steps = [0, 1 * DAY, 2 * DAY + 3 * HOUR, 6 * DAY, 9 * DAY];
		var list = steps.map(function (step, i) {
			return {
				id: fileId + '-v' + (steps.length - i),
				kind: 'version',
				author: authors[i].name,
				at: ago(e[5] + step),
				bytes: Math.round(e[6] * (1 - i * 0.04)),
				note: i === steps.length - 1 ? 'Added to Armory' : 'Saved',
				releaseNotChecked: false,
				isCurrent: i === 0
			};
		});
		return extra ? extra(list) : list;
	}

	function detail(fileId, change, extraHistory) {
		var e = catalogEntry(fileId);
		var row = fileRow(e, change);
		return {
			fileId: row.fileId,
			name: row.name,
			path: row.path,
			project: projectOf(e[1]).name,
			folder: e[2],
			status: row.status,
			holder: row.holder,
			releaseNotChecked: row.releaseNotChecked,
			history: history(fileId, extraHistory)
		};
	}

	var SETTINGS = { vaultRoot: 'C:\\IDEA\\Armory', startAtSignIn: true, theme: 'system' };

	function signedIn(parts) {
		return {
			connection: 'signedIn',
			connect: { phase: 'idle', message: null },
			account: { email: ME.email, deviceName: ME.device },
			sync: parts.sync,
			vaultRoot: SETTINGS.vaultRoot,
			myFiles: parts.myFiles || [],
			needsMe: parts.needsMe || [],
			projects: projects(parts.changes),
			settings: { vaultRoot: SETTINGS.vaultRoot, startAtSignIn: SETTINGS.startAtSignIn, theme: SETTINGS.theme },
			effectiveTheme: 'idea'
		};
	}

	function notSignedIn(connection, phase, message) {
		return {
			connection: connection,
			connect: { phase: phase, message: message },
			account: null,
			sync: { state: 'paused', line: 'Not connected yet.', detail: null, pendingCount: 0 },
			vaultRoot: SETTINGS.vaultRoot,
			myFiles: [],
			needsMe: [],
			projects: [],
			settings: { vaultRoot: SETTINGS.vaultRoot, startAtSignIn: SETTINGS.startAtSignIn, theme: SETTINGS.theme },
			effectiveTheme: 'idea'
		};
	}

	function myFile(fileId, status, note, path) {
		var e = fileId ? catalogEntry(fileId) : null;
		var p = path || pathOf(e);
		return {
			fileId: fileId,
			path: p,
			name: p.split('/').pop(),
			project: p.split('/')[0],
			status: status,
			note: note
		};
	}

	// Changes every signed-in state shares: Alex is working on the intake.
	var ALEX_ON_INTAKE = { status: 'editingByOther', holder: holder(ALEX, 40 * MIN, true) };

	function withShared(changes) {
		var out = { 'f-intake-asm': ALEX_ON_INTAKE };
		for (var k in changes) out[k] = changes[k];
		return out;
	}

	var MY_GEARBOX = { status: 'editingByMe', holder: holder(ME, 35 * MIN, true) };
	var MARIA_ON_PLATE = { status: 'editingByOther', holder: holder(MARIA, 25 * MIN, false) };

	var states = {
		signedOut: {
			label: 'First run, before connecting',
			screens: ['connect'],
			view: notSignedIn('signedOut', 'idle', null)
		},

		connecting: {
			label: 'Waiting for the browser sign-in',
			screens: ['connect'],
			view: notSignedIn(
				'connecting',
				'waitingForBrowser',
				'Finish signing in with your school Google account in the browser. This window updates by itself.'
			)
		},

		synced: {
			label: 'Everything saved',
			screens: ['home', 'settings'],
			view: signedIn({
				sync: { state: 'synced', line: 'Everything is saved to Armory.', detail: 'Last checked 2 minutes ago', pendingCount: 0 },
				myFiles: [myFile('f-gearbox', 'editingByMe', "You're editing this. It saves to Armory each time you save in SolidWorks.")],
				changes: withShared({ 'f-gearbox': MY_GEARBOX })
			})
		},

		syncing: {
			label: 'Sending and getting changes',
			screens: ['home'],
			view: signedIn({
				sync: {
					state: 'syncing',
					line: 'Sending 2 changes to Armory.',
					detail: "Getting 1 newer file from your team too. You can keep working.",
					pendingCount: 2
				},
				myFiles: [
					myFile('f-gearbox', 'syncing', 'Sending your latest save.'),
					myFile('f-wheel-hub', 'syncing', 'Sending your latest save.')
				],
				changes: withShared({
					'f-gearbox': { status: 'syncing', holder: holder(ME, 35 * MIN, false) },
					'f-wheel-hub': { status: 'syncing', updatedBy: JORDAN.name, updatedAt: ago(1 * MIN) },
					'f-plate-right': { status: 'syncing' },
					'f-stage-tube': { status: 'notOnThisComputer' }
				})
			})
		},

		offline: {
			label: 'No internet, work waiting to send',
			screens: ['home'],
			view: signedIn({
				sync: {
					state: 'offline',
					line: "You're offline. Your work is safe on this computer.",
					detail: "3 changes will send when you're back online.",
					pendingCount: 3
				},
				myFiles: [
					myFile('f-gearbox', 'waitingToSend', 'Saved on this computer. Waiting to send.'),
					myFile('f-plate-left', 'waitingToSend', 'Saved on this computer. Waiting to send.'),
					myFile(null, 'waitingToSend', "New file. It goes to Armory when you're back online.", 'Robot 2027/Intake/Intake-Gearbox.SLDASM')
				],
				changes: withShared({
					'f-gearbox': { status: 'waitingToSend', holder: holder(ME, 50 * MIN, false) },
					'f-plate-left': { status: 'waitingToSend', holder: holder(ME, 30 * MIN, false) }
				})
			})
		},

		conflict: {
			label: 'Kept as your own copy, and a newer version waiting',
			screens: ['home', 'detail'],
			detailFileId: 'f-plate-left',
			view: signedIn({
				sync: { state: 'attention', line: '2 things need you.', detail: 'Everything else is saved to Armory.', pendingCount: 0 },
				needsMe: [
					{
						kind: 'sideVersion',
						fileId: 'f-plate-left',
						path: 'Robot 2027/Drivetrain/Plate-Left.SLDPRT',
						name: 'Plate-Left.SLDPRT',
						title: 'Kept as your own copy',
						detail: 'Maria saved Plate-Left.SLDPRT while you were offline, so your changes were kept as your own copy. Nothing was lost. Ask Maria or your CAD lead which one to keep.',
						at: ago(12 * MIN)
					},
					{
						kind: 'newerWaiting',
						fileId: 'f-gearbox',
						path: 'Robot 2027/Drivetrain/Gearbox.SLDASM',
						name: 'Gearbox.SLDASM',
						title: 'A newer version from Maria is waiting',
						detail: 'Close Gearbox.SLDASM in SolidWorks to get it.',
						at: ago(4 * MIN)
					}
				],
				myFiles: [myFile('f-plate-left', 'conflict', 'Kept as your own copy. Nothing was lost.')],
				changes: withShared({
					'f-plate-left': { status: 'conflict', updatedAt: ago(14 * MIN) },
					'f-gearbox': { status: 'newerWaiting', updatedBy: MARIA.name, updatedAt: ago(4 * MIN) }
				})
			}),
			details: {
				'f-plate-left': detail('f-plate-left', { status: 'conflict', updatedAt: ago(14 * MIN) }, function (list) {
					list[0].at = ago(14 * MIN);
					list.splice(1, 0, {
						id: 'f-plate-left-side-1',
						kind: 'sideVersion',
						author: JORDAN.name,
						at: ago(12 * MIN),
						bytes: 618004,
						note: 'Kept as your own copy: Maria saved first',
						releaseNotChecked: false,
						isCurrent: false
					});
					return list;
				})
			}
		},

		refused: {
			label: "Uploads Armory can't take",
			screens: ['home'],
			view: signedIn({
				sync: { state: 'attention', line: "2 files can't be sent.", detail: 'Everything else is saved to Armory. Your changes are safe on this computer.', pendingCount: 2 },
				needsMe: [
					{
						kind: 'refused',
						fileId: 'f-bracket',
						path: 'Robot 2027/Intake/Bracket.SLDPRT',
						name: 'Bracket.SLDPRT',
						title: "Can't send Bracket.SLDPRT",
						detail: 'It was saved in SolidWorks 2026, and the team uses 2025. In SolidWorks, use Save As and pick 2025, then it sends by itself.',
						at: ago(9 * MIN)
					},
					{
						kind: 'nameTaken',
						fileId: null,
						path: 'Robot 2027/Intake/Gearbox.SLDASM',
						name: 'Gearbox.SLDASM',
						title: 'That name is already taken',
						detail: 'Robot 2027 already has a Gearbox.SLDASM in Drivetrain. Give your new one a different name, like Intake-Gearbox.SLDASM.',
						at: ago(3 * MIN)
					}
				],
				myFiles: [
					myFile('f-bracket', 'refused', "Can't send this one. It was saved in SolidWorks 2026."),
					myFile(null, 'refused', 'This name is already used in Drivetrain.', 'Robot 2027/Intake/Gearbox.SLDASM')
				],
				changes: withShared({ 'f-bracket': { status: 'refused', holder: holder(ME, 30 * MIN, false) } })
			})
		},

		lockedByOther: {
			label: 'Someone else is editing a file you opened',
			screens: ['home', 'detail'],
			detailFileId: 'f-plate-left',
			view: signedIn({
				sync: { state: 'synced', line: 'Everything is saved to Armory.', detail: 'Last checked 1 minute ago', pendingCount: 0 },
				myFiles: [
					myFile(
						'f-plate-left',
						'editingByOther',
						"Maria Lopez is editing this. You can look at it, but you can't save changes until Maria is done."
					)
				],
				changes: withShared({ 'f-plate-left': MARIA_ON_PLATE })
			}),
			details: {
				'f-plate-left': detail('f-plate-left', MARIA_ON_PLATE)
			}
		},

		releaseNotChecked: {
			label: "Saved, but the SolidWorks version couldn't be checked",
			screens: ['home', 'detail'],
			detailFileId: 'f-wheel-hub',
			view: signedIn({
				sync: { state: 'attention', line: 'Saved to Armory. 1 thing needs a look.', detail: 'Last checked 1 minute ago', pendingCount: 0 },
				needsMe: [
					{
						kind: 'releaseNotChecked',
						fileId: 'f-wheel-hub',
						path: 'Robot 2027/Drivetrain/Wheel-Hub.SLDPRT',
						name: 'Wheel-Hub.SLDPRT',
						title: "Couldn't check the SolidWorks version",
						detail: "Wheel-Hub.SLDPRT is saved to Armory, but Armory couldn't tell which SolidWorks made it. If it won't open on a lab computer, open it in SolidWorks 2025 and save it again.",
						at: ago(15 * MIN)
					}
				],
				myFiles: [],
				changes: withShared({
					'f-wheel-hub': { releaseNotChecked: true, updatedBy: JORDAN.name, updatedAt: ago(15 * MIN) }
				})
			}),
			details: {
				'f-wheel-hub': detail('f-wheel-hub', { releaseNotChecked: true, updatedBy: JORDAN.name, updatedAt: ago(15 * MIN) }, function (list) {
					list[0].author = JORDAN.name;
					list[0].at = ago(15 * MIN);
					list[0].releaseNotChecked = true;
					list[0].note = 'Saved';
					return list;
				})
			}
		},

		paused: {
			label: 'Paused by the student',
			screens: ['home'],
			view: signedIn({
				sync: {
					state: 'paused',
					line: "Paused. Nothing is sent or received until you resume.",
					detail: '1 change is waiting on this computer.',
					pendingCount: 1
				},
				myFiles: [myFile('f-gearbox', 'waitingToSend', 'Saved on this computer. Waiting to send.')],
				changes: withShared({ 'f-gearbox': { status: 'waitingToSend', holder: holder(ME, 50 * MIN, false) } })
			})
		},

		connectFailed: {
			label: "The browser sign-in didn't finish",
			screens: ['connect'],
			view: notSignedIn('signedOut', 'failed', "That sign-in didn't finish. Check that you're online, then try again.")
		},

		vaultOwnedByOther: {
			label: 'The Armory folder belongs to another account',
			screens: ['connect'],
			view: (function () {
				var v = notSignedIn(
					'vaultOwnedByOther',
					'idle',
					'The folder C:\\IDEA\\Armory already holds files for alex.kim@boscotech.edu. Pick a different folder for your files, or sign out.'
				);
				v.account = { email: ME.email, deviceName: ME.device };
				return v;
			})()
		}
	};

	// Detail for any file not written out above: built from the state's own row.
	function detailFor(stateName, fileId) {
		var s = states[stateName];
		if (s && s.details && s.details[fileId]) return s.details[fileId];
		var view = s ? s.view : null;
		var row = null;
		if (view) {
			view.projects.forEach(function (p) {
				p.folders.forEach(function (f) {
					f.files.forEach(function (r) {
						if (r.fileId === fileId) row = r;
					});
				});
			});
		}
		var d = detail(fileId, row ? { status: row.status, holder: row.holder, releaseNotChecked: row.releaseNotChecked } : null);
		if (row && row.updatedAt) d.history[0].at = row.updatedAt;
		if (row && row.updatedBy) d.history[0].author = row.updatedBy;
		return d;
	}

	window.ArmoryDemoStates = {
		now: NOW,
		defaultState: 'synced',
		/** The state the demo starts in after "Connect this computer" finishes. */
		afterConnect: 'synced',
		states: states,
		detailFor: detailFor,
		/** Where a demo "Change" folder picker lands. */
		pickedVaultRoot: 'D:\\School\\Armory'
	};
})();
