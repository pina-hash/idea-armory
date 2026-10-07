/*
 * Demo states for the Armory Agent window.
 *
 * Used only outside WebView2 (a plain browser, the screenshot tools). bridge.js loads
 * this file when window.chrome.webview is missing and answers the page from it, so the
 * page never knows the difference. Every view below is a complete AgentView as
 * docs/agent/BRIDGE.md defines it; every detail is a complete FileDetailView. Where
 * docs/agent v2-design gives the engine's own sentence (an activity line, a notice
 * title, a check-out label), the demo uses it word for word.
 *
 * The clock is fixed (NOW) so relative times ("5 minutes ago") and screenshots are
 * stable. The people, projects and files are FRC Team 5669 and an IDEA class at Bosco
 * Tech; the people are made up.
 *
 * Each state says which screens show it best (`screens`), which file to open on File
 * detail (`detailFileId`), and any page-only place to open on (`params`: project,
 * folder, select, expand, dialog, drag, at; bridge.js reads them from the query string).
 * tools/agent-ui reads all three to decide what to render.
 */
(function () {
	'use strict';

	var NOW = '2026-10-01T15:30:00-07:00';
	var NOW_MS = Date.parse(NOW);
	var MIN = 1;
	var HOUR = 60;
	var DAY = 24 * 60;
	var MB = 1024 * 1024;
	var GB = 1024 * MB;

	/** ISO-8601 time `minutes` before the demo clock. */
	function ago(minutes) {
		return new Date(NOW_MS - minutes * 60000).toISOString();
	}

	// People. "Me" is the student signed in on this computer.
	var ME = { name: 'Jordan Reyes', email: 'jordan.reyes@boscotech.edu', device: 'LAB-PC-14' };
	var MARIA = { name: 'Maria Lopez', email: 'maria.lopez@boscotech.edu', device: 'LAB-PC-07' };
	var ALEX = { name: 'Alex Kim', email: 'alex.kim@boscotech.edu', device: 'ALEX-LAPTOP' };
	var SAM = { name: 'Sam Patel', email: 'sam.patel@boscotech.edu', device: 'LAB-PC-03' };
	var PINA = { name: 'Mr. Pina', email: 'pina@boscotech.edu', device: 'ROOM-209-PC' };

	/* ------------------------------------------------------ Check outs */

	function available() {
		return { state: 'available', label: 'Available', name: null, email: null, device: null, since: null };
	}
	function mine(minutesAgo) {
		return { state: 'mine', label: 'Checked out by you', name: ME.name, email: ME.email, device: ME.device, since: ago(minutesAgo) };
	}
	function other(person, minutesAgo) {
		return {
			state: 'other',
			label: 'Checked out by ' + person.name + ' on ' + person.device,
			name: person.name,
			email: person.email,
			device: person.device,
			since: ago(minutesAgo)
		};
	}
	function myOther(minutesAgo) {
		return { state: 'myOtherComputer', label: 'Checked out by you on JORDAN-LAPTOP', name: ME.name, email: ME.email, device: 'JORDAN-LAPTOP', since: ago(minutesAgo) };
	}

	/* ------------------------------------------------- Projects and files */

	var PROJECTS = [
		{ id: 'proj-robot-2027', name: 'Robot 2027', role: 'student', folders: ['', 'Drivetrain', 'Drivetrain/Gearbox', 'Intake', 'Elevator', 'COTS'] },
		{ id: 'proj-idea-209', name: 'IDEA 209 Bridge', role: 'student', folders: ['', 'Trusses', 'Drawings'] }
	];
	var ARCHIVED = { id: 'proj-robot-2026', name: 'Robot 2026', role: 'student', folders: ['', 'Chassis'] };

	// Every file on the server: id, project, folder, name, last checked in by, minutes ago, size.
	var CATALOG = [
		['f-robot-asm', 'proj-robot-2027', '', 'Robot-2027.SLDASM', ALEX, 2 * DAY, 3870210],
		['f-gearbox', 'proj-robot-2027', 'Drivetrain', 'Gearbox.SLDASM', ME, 1 * DAY, 1482113],
		['f-plate-left', 'proj-robot-2027', 'Drivetrain', 'Plate-Left.SLDPRT', MARIA, 1 * HOUR, 612480],
		['f-plate-right', 'proj-robot-2027', 'Drivetrain', 'Plate-Right.SLDPRT', MARIA, 70 * MIN, 609312],
		['f-wheel-hub', 'proj-robot-2027', 'Drivetrain', 'Wheel-Hub.SLDPRT', ALEX, 3 * HOUR, 284766],
		['f-dt-drawing', 'proj-robot-2027', 'Drivetrain', 'Drivetrain.SLDDRW', ME, 1 * DAY, 951300],
		['f-gear-14', 'proj-robot-2027', 'Drivetrain/Gearbox', 'Spur-Gear-14T.SLDPRT', ALEX, 2 * DAY, 210442],
		['f-gear-60', 'proj-robot-2027', 'Drivetrain/Gearbox', 'Spur-Gear-60T.SLDPRT', ALEX, 2 * DAY, 498120],
		['f-gear-shaft', 'proj-robot-2027', 'Drivetrain/Gearbox', 'Gear-Shaft.SLDPRT', SAM, 3 * DAY, 120877],
		['f-intake-asm', 'proj-robot-2027', 'Intake', 'Intake.SLDASM', ALEX, 5 * HOUR, 2210004],
		['f-bracket', 'proj-robot-2027', 'Intake', 'Bracket.SLDPRT', ALEX, 4 * DAY, 198442],
		['f-roller-shaft', 'proj-robot-2027', 'Intake', 'Roller-Shaft.SLDPRT', MARIA, 2 * DAY, 120931],
		['f-elevator-asm', 'proj-robot-2027', 'Elevator', 'Elevator.SLDASM', MARIA, 6 * HOUR, 2650177],
		['f-carriage', 'proj-robot-2027', 'Elevator', 'Carriage-Plate.SLDPRT', ME, 2 * DAY, 455030],
		['f-stage-tube', 'proj-robot-2027', 'Elevator', 'Stage-1-Tube.SLDPRT', ALEX, 3 * DAY, 141876],
		['f-hex-bearing', 'proj-robot-2027', 'COTS', 'Hex-Bearing-0.5in.SLDPRT', PINA, 20 * DAY, 88213],
		['f-motor', 'proj-robot-2027', 'COTS', 'Motor-Mount.SLDPRT', PINA, 20 * DAY, 301455],
		['f-collar', 'proj-robot-2027', 'COTS', 'Shaft-Collar.SLDPRT', PINA, 21 * DAY, 64220],
		['f-bridge-asm', 'proj-idea-209', '', 'Bridge.SLDASM', ME, 3 * DAY, 1310442],
		['f-truss-side', 'proj-idea-209', 'Trusses', 'Truss-Side.SLDPRT', ME, 3 * DAY, 402118],
		['f-gusset', 'proj-idea-209', 'Trusses', 'Gusset-Plate.SLDPRT', MARIA, 4 * DAY, 99876],
		['f-cross-brace', 'proj-idea-209', 'Trusses', 'Cross-Brace.SLDPRT', ALEX, 4 * DAY, 87021],
		['f-bridge-drawing', 'proj-idea-209', 'Drawings', 'Bridge-Drawing.SLDDRW', ME, 5 * DAY, 733904],
		['f-chassis-asm', 'proj-robot-2026', '', 'Robot-2026.SLDASM', ALEX, 200 * DAY, 3410221],
		['f-chassis-rail', 'proj-robot-2026', 'Chassis', 'Chassis-Rail.SLDPRT', MARIA, 210 * DAY, 288104]
	];

	// A Pack and Go folder for the big states: generated names, same shapes every run.
	var BASES = ['Bracket', 'Plate', 'Spacer', 'Shaft', 'Gusset', 'Bearing-Block', 'Standoff', 'Hub', 'Tube', 'Rail'];
	function pad4(n) {
		return ('000' + n).slice(-4);
	}
	function generated(count, folder, prefix) {
		var out = [];
		for (var i = 1; i <= count; i++) {
			var ext = i % 25 === 0 ? '.SLDASM' : i % 40 === 0 ? '.SLDDRW' : '.SLDPRT';
			var name = (i % 25 === 0 ? 'Sub-Assembly' : BASES[i % BASES.length]) + '-' + pad4(i) + ext;
			out.push([prefix + '-' + pad4(i), 'proj-robot-2027', folder, name, i % 3 ? ALEX : ME, 40 + (i % 50), 40000 + ((i * 7919) % 900000)]);
		}
		return out;
	}
	var BIG = generated(5000, 'CopyDesignTemp', 'f-cdt');
	var IMPORTED = generated(42, 'CopyDesignTemp', 'f-imp');
	var EXTRA = {};
	BIG.concat(IMPORTED).forEach(function (e) {
		EXTRA[e[0]] = e;
	});

	function catalogEntry(fileId) {
		for (var i = 0; i < CATALOG.length; i++) if (CATALOG[i][0] === fileId) return CATALOG[i];
		if (EXTRA[fileId]) return EXTRA[fileId];
		throw new Error('No demo file ' + fileId);
	}
	function projectOf(projectId) {
		if (projectId === ARCHIVED.id) return ARCHIVED;
		for (var i = 0; i < PROJECTS.length; i++) if (PROJECTS[i].id === projectId) return PROJECTS[i];
		throw new Error('No demo project ' + projectId);
	}
	function pathOf(entry) {
		var project = projectOf(entry[1]);
		return project.name + '/' + (entry[2] ? entry[2] + '/' : '') + entry[3];
	}

	/** The FileRowView for one catalog entry, with this state's changes applied. */
	function fileRow(entry, change) {
		var row = {
			fileId: entry[0],
			name: entry[3],
			path: pathOf(entry),
			status: 'synced',
			checkout: available(),
			changed: false,
			releaseNotChecked: false,
			updatedAt: ago(entry[5]),
			updatedBy: entry[4].name
		};
		if (change) for (var k in change) row[k] = change[k];
		return row;
	}

	/**
	 * Projects and folders as the server and this computer see them. `opts`:
	 *   files       extra catalog entries (a Pack and Go folder)
	 *   folders     extra folder paths, by project id
	 *   local       files in a folder that aren't in Armory: [projectId, folder, name]
	 *   archived    include the archived Robot 2026
	 *   lead        this account is a mentor in Robot 2027 (may take back)
	 */
	function projects(changes, opts) {
		changes = changes || {};
		opts = opts || {};
		var list = PROJECTS.slice();
		if (opts.archived) list.push(ARCHIVED);
		var catalog = CATALOG.concat(opts.files || []);
		return list.map(function (p) {
			var lead = !!opts.lead && p.id === 'proj-robot-2027';
			var folders = p.folders.concat((opts.folders || {})[p.id] || []);
			return {
				id: p.id,
				name: p.name,
				archived: p === ARCHIVED,
				role: lead ? 'mentor' : p.role,
				canTakeBack: lead,
				folders: folders.map(function (folder) {
					var files = catalog
						.filter(function (e) {
							return e[1] === p.id && e[2] === folder;
						})
						.map(function (e) {
							return fileRow(e, changes[e[0]]);
						});
					(opts.local || []).forEach(function (l) {
						if (l[0] === p.id && l[1] === folder)
							files.push({
								fileId: null,
								name: l[2],
								path: p.name + '/' + (folder ? folder + '/' : '') + l[2],
								status: 'notInArmory',
								checkout: available(),
								changed: false,
								releaseNotChecked: false,
								updatedAt: null,
								updatedBy: null
							});
					});
					return { path: folder, name: folder ? folder.split('/').pop() : p.name, fileCount: files.length, files: files };
				})
			};
		});
	}

	/** My files: the files this computer has checked out, in every project (addendum 7). */
	function myFilesOf(view, notes) {
		var out = [];
		view.projects.forEach(function (p) {
			p.folders.forEach(function (f) {
				f.files.forEach(function (r) {
					if (r.checkout.state !== 'mine') return;
					out.push({
						fileId: r.fileId,
						path: r.path,
						name: r.name,
						project: p.name,
						status: r.status,
						note: (notes || {})[r.fileId || r.path] || null,
						checkout: r.checkout
					});
				});
			});
		});
		return out;
	}

	/* ----------------------------------------------------------- Activity */

	function idle() {
		return { line: null, upload: null, download: null, move: null, waiting: null, active: [] };
	}
	function direction(filesDone, filesTotal, bytesDone, bytesTotal, perSecond, secondsLeft, line) {
		return { filesDone: filesDone, filesTotal: filesTotal, bytesDone: bytesDone, bytesTotal: bytesTotal, bytesPerSecond: perSecond, secondsLeft: secondsLeft, line: line };
	}
	function active(fileId, dir, done, total) {
		var e = catalogEntry(fileId);
		return { path: pathOf(e), name: e[3], direction: dir, bytesDone: done, bytesTotal: total };
	}

	/* ------------------------------------------------------------- Notices */

	function notice(key, kind, tone, title, detail, count, action, items) {
		return { key: key, kind: kind, tone: tone, title: title, detail: detail, count: count, action: action, items: items || [] };
	}
	function item(fileId, path, detail) {
		return { fileId: fileId, path: path, name: path.split('/').pop(), detail: detail || null };
	}

	// 14 files from an unzipped Pack and Go whose names the project already has.
	var SHARED_NAMES = [
		['Bracket.SLDPRT', 'Intake'],
		['Plate-Left.SLDPRT', 'Drivetrain'],
		['Plate-Right.SLDPRT', 'Drivetrain'],
		['Wheel-Hub.SLDPRT', 'Drivetrain'],
		['Gearbox.SLDASM', 'Drivetrain'],
		['Roller-Shaft.SLDPRT', 'Intake'],
		['Carriage-Plate.SLDPRT', 'Elevator'],
		['Stage-1-Tube.SLDPRT', 'Elevator'],
		['Hex-Bearing-0.5in.SLDPRT', 'COTS'],
		['Motor-Mount.SLDPRT', 'COTS'],
		['Shaft-Collar.SLDPRT', 'COTS'],
		['Spur-Gear-14T.SLDPRT', 'Drivetrain/Gearbox'],
		['Spur-Gear-60T.SLDPRT', 'Drivetrain/Gearbox'],
		['Gear-Shaft.SLDPRT', 'Drivetrain/Gearbox']
	];
	function sharedNameItems(folder, n) {
		return SHARED_NAMES.slice(0, n).map(function (s) {
			return item(null, 'Robot 2027/' + folder + '/' + s[0], 'Robot 2027 already has ' + s[0] + ' in ' + s[1] + '.');
		});
	}
	function nameShared(folder, n) {
		return notice(
			'nameShared',
			'nameShared',
			'look',
			n + ' files share a name with other files in this project',
			'A project keeps one file per name, because SolidWorks finds parts by name. Rename these to add them.',
			n,
			{ label: 'Show them', command: 'expand', paths: [] },
			sharedNameItems(folder, n)
		);
	}

	/* ------------------------------------------------------------- Views */

	var SETTINGS = { vaultRoot: 'C:\\IDEA\\Armory', startAtSignIn: true, theme: 'system' };

	function signedIn(parts) {
		var view = {
			connection: 'signedIn',
			connect: { phase: 'idle', message: null },
			account: { email: ME.email, deviceName: ME.device },
			sync: parts.sync,
			activity: parts.activity || idle(),
			vaultRoot: SETTINGS.vaultRoot,
			notices: parts.notices || [],
			prompt: parts.prompt || null,
			myFiles: [],
			projects: projects(parts.changes, parts.opts),
			settings: { vaultRoot: SETTINGS.vaultRoot, startAtSignIn: SETTINGS.startAtSignIn, theme: SETTINGS.theme },
			effectiveTheme: 'idea'
		};
		view.myFiles = myFilesOf(view, parts.notes);
		return view;
	}

	function notSignedIn(connection, phase, message) {
		return {
			connection: connection,
			connect: { phase: phase, message: message },
			account: null,
			sync: { state: 'paused', line: 'Not connected yet.', detail: null, pendingCount: 0 },
			activity: idle(),
			vaultRoot: SETTINGS.vaultRoot,
			notices: [],
			prompt: null,
			myFiles: [],
			projects: [],
			settings: { vaultRoot: SETTINGS.vaultRoot, startAtSignIn: SETTINGS.startAtSignIn, theme: SETTINGS.theme },
			effectiveTheme: 'idea'
		};
	}

	var SYNCED = { state: 'synced', line: 'Everything is saved to Armory.', detail: 'Last checked 2 minutes ago.', pendingCount: 0 };

	function pausedSync(pending) {
		return {
			state: 'paused',
			line: 'Paused. Nothing is uploaded or downloaded until you resume.',
			detail: pending ? (pending === 1 ? '1 file is waiting to upload.' : pending + ' files are waiting to upload.') : null,
			pendingCount: pending
		};
	}

	// Changes every signed-in state shares: Alex has the intake checked out, and I have the
	// gearbox.
	function shared(changes) {
		var out = { 'f-intake-asm': { checkout: other(ALEX, 40 * MIN) }, 'f-gearbox': { checkout: mine(35 * MIN) } };
		for (var k in changes) out[k] = changes[k];
		return out;
	}

	var MARIA_HAS_PLATE = { checkout: other(MARIA, 25 * MIN), updatedAt: ago(1 * HOUR) };

	var TRANSFERS = {
		line: 'Downloading 412 of 1,280 files, 2.1 GB left, about 3 min',
		download: direction(412, 1280, Math.round(1.4 * GB), Math.round(3.5 * GB), Math.round(12.6 * MB), 170, 'Downloading 412 of 1,280 files, 2.1 GB left, about 3 min'),
		upload: direction(3, 9, Math.round(30 * MB), Math.round(78 * MB), Math.round(2.4 * MB), 20, 'Uploading 3 of 9 files, 48 MB left, about 20 sec'),
		move: direction(45, 120, 0, 0, 0, null, 'Moving 120 files to Gearbox'),
		waiting: null,
		active: [
			active('f-elevator-asm', 'download', Math.round(1.9 * MB), 2650177),
			active('f-stage-tube', 'download', 41000, 141876),
			active('f-carriage', 'download', 402000, 455030),
			active('f-intake-asm', 'download', 260000, 2210004),
			active('f-gearbox', 'upload', 1210000, 1482113),
			active('f-dt-drawing', 'upload', 120000, 951300)
		]
	};

	var states = {
		signedOut: {
			label: 'First run, before connecting',
			screens: ['connect'],
			view: notSignedIn('signedOut', 'idle', null)
		},

		connecting: {
			label: 'Waiting for the browser sign-in',
			screens: ['connect'],
			view: notSignedIn('connecting', 'waitingForBrowser', 'Finish signing in with your school Google account in the browser. This window updates by itself.')
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
		},

		synced: {
			label: 'Everything up to date: the file browser at a project\'s top folder',
			screens: ['home', 'detail', 'settings'],
			detailFileId: 'f-wheel-hub',
			view: signedIn({ sync: SYNCED, changes: shared({}) })
		},

		checkedOutByMe: {
			label: 'Files you checked out, one with changes not checked in',
			screens: ['home', 'detail'],
			detailFileId: 'f-gearbox',
			params: { folder: 'Drivetrain' },
			view: signedIn({
				sync: SYNCED,
				activity: {
					line: null,
					upload: null,
					download: null,
					move: null,
					waiting: { count: 2, line: '2 checked-out files have changes. Check them in to share them.' },
					active: []
				},
				changes: shared({
					'f-gearbox': { checkout: mine(35 * MIN), changed: true, status: 'changed', releaseNotChecked: true },
					'f-plate-right': { checkout: mine(2 * HOUR) },
					'f-truss-side': { checkout: mine(1 * DAY), changed: true, status: 'changed' }
				})
			}),
			details: {
				'f-gearbox': function (d) {
					d.history.unshift(
						{ id: 'f-gearbox-k2', kind: 'keptCopy', author: ME.name, at: ago(6 * MIN), bytes: 1490022, note: 'Saved while checked out', releaseNotChecked: true, isCurrent: false, routine: true },
						{ id: 'f-gearbox-k1', kind: 'keptCopy', author: ME.name, at: ago(28 * MIN), bytes: 1486610, note: 'Saved while checked out', releaseNotChecked: false, isCurrent: false, routine: true }
					);
					return d;
				}
			}
		},

		checkedOutByOther: {
			label: 'Maria has a plate checked out: you can look, not save',
			screens: ['home', 'detail'],
			detailFileId: 'f-plate-left',
			params: { folder: 'Drivetrain', at: 'browser' },
			view: signedIn({ sync: SYNCED, changes: shared({ 'f-plate-left': MARIA_HAS_PLATE }) })
		},

		transferring: {
			label: 'Uploading, downloading and moving at once, each file with its own bar',
			screens: ['home'],
			view: signedIn({
				sync: { state: 'syncing', line: 'Downloading 412 of 1,280 files, 2.1 GB left, about 3 min', detail: 'You can keep working.', pendingCount: 6 },
				activity: TRANSFERS,
				changes: shared({
					'f-gearbox': { checkout: mine(35 * MIN), status: 'uploading', changed: true },
					'f-dt-drawing': { checkout: mine(50 * MIN), status: 'uploading', changed: true },
					'f-elevator-asm': { status: 'downloading' },
					'f-stage-tube': { status: 'downloading' },
					'f-carriage': { status: 'downloading' },
					'f-hex-bearing': { status: 'notOnThisComputer' }
				})
			})
		},

		offlineWaiting: {
			label: 'Offline, with files waiting to upload',
			screens: ['home'],
			view: signedIn({
				sync: { state: 'offline', line: "You're offline. Your work is safe on this computer.", detail: null, pendingCount: 3 },
				activity: {
					line: null,
					upload: null,
					download: null,
					move: null,
					waiting: { count: 3, line: '3 files are waiting to upload. They upload when this computer is back online.' },
					active: []
				},
				changes: shared({
					'f-gearbox': { checkout: mine(50 * MIN), status: 'waiting', changed: true },
					'f-plate-right': { checkout: mine(30 * MIN), status: 'waiting', changed: true }
				}),
				opts: { local: [['proj-robot-2027', 'Intake', 'Intake-Gearbox.SLDASM']] }
			})
		},

		pausedWaiting: {
			label: 'Paused by the student, with files waiting',
			screens: ['home'],
			view: signedIn({
				sync: pausedSync(2),
				activity: {
					line: null,
					upload: null,
					download: null,
					move: null,
					waiting: { count: 2, line: '2 files are waiting to upload. They upload when you resume.' },
					active: []
				},
				changes: shared({ 'f-gearbox': { checkout: mine(50 * MIN), status: 'waiting', changed: true } })
			})
		},

		groupedNotices: {
			label: 'Notices grouped by kind, one card each, lists closed',
			screens: ['home', 'detail'],
			detailFileId: 'f-plate-left',
			view: groupedView()
		},

		groupedNoticesExpanded: {
			label: 'Notices grouped by kind, one list open',
			screens: ['home'],
			params: { expand: 'nameShared' },
			view: groupedView()
		},

		importSummary: {
			label: 'One summary after unzipping a Pack and Go',
			screens: ['home'],
			params: { folder: 'CopyDesignTemp' },
			view: signedIn({
				sync: { state: 'attention', line: 'Everything else is saved. A few files need you.', detail: 'Last checked just now.', pendingCount: 0 },
				notices: [
					notice(
						'import',
						'import',
						'info',
						'Added 4,987 of 5,000 files to Robot 2027 > CopyDesignTemp',
						'12 of them were brought back with their history. 13 need you: they share a name with other files in this project.',
						5000,
						{ label: 'Done', command: 'dismissNotice', paths: [] },
						sharedNameItems('CopyDesignTemp', 13)
					),
					nameShared('CopyDesignTemp', 13)
				],
				changes: shared({}),
				opts: {
					files: IMPORTED,
					folders: { 'proj-robot-2027': ['CopyDesignTemp'] },
					local: SHARED_NAMES.slice(0, 13).map(function (s) {
						return ['proj-robot-2027', 'CopyDesignTemp', s[0]];
					})
				}
			})
		},

		checkoutPrompt: {
			label: 'SolidWorks opened a file you have not checked out',
			screens: ['home'],
			params: { folder: 'Drivetrain' },
			view: signedIn({
				sync: SYNCED,
				prompt: {
					key: 'prompt:Robot 2027/Drivetrain/Plate-Left.SLDPRT:' + ago(2 * MIN),
					fileId: 'f-plate-left',
					path: 'Robot 2027/Drivetrain/Plate-Left.SLDPRT',
					name: 'Plate-Left.SLDPRT',
					checkout: available(),
					canCheckOut: true
				},
				changes: shared({})
			})
		},

		checkoutPromptTaken: {
			label: 'SolidWorks opened a file someone else has checked out',
			screens: ['home'],
			params: { folder: 'Drivetrain' },
			view: signedIn({
				sync: SYNCED,
				prompt: {
					key: 'prompt:Robot 2027/Drivetrain/Plate-Left.SLDPRT:' + ago(2 * MIN),
					fileId: 'f-plate-left',
					path: 'Robot 2027/Drivetrain/Plate-Left.SLDPRT',
					name: 'Plate-Left.SLDPRT',
					checkout: other(MARIA, 25 * MIN),
					canCheckOut: false
				},
				changes: shared({ 'f-plate-left': MARIA_HAS_PLATE })
			})
		},

		folderPutBack: {
			label: 'A folder rename was put back: someone has files in it checked out',
			screens: ['home'],
			params: { folder: 'Drivetrain' },
			view: signedIn({
				sync: { state: 'attention', line: 'Everything else is saved. A few files need you.', detail: 'Last checked just now.', pendingCount: 0 },
				notices: [
					notice(
						'folderPutBack',
						'folderPutBack',
						'look',
						'Gearbox was put back: Maria Lopez has 2 of its files checked out.',
						'A folder is renamed or deleted only when nobody else has a file in it checked out. Ask Maria to check them in, then try again.',
						2,
						null,
						[
							item('f-gear-14', 'Robot 2027/Drivetrain/Gearbox/Spur-Gear-14T.SLDPRT', 'Checked out by Maria Lopez on LAB-PC-07'),
							item('f-gear-60', 'Robot 2027/Drivetrain/Gearbox/Spur-Gear-60T.SLDPRT', 'Checked out by Maria Lopez on LAB-PC-07')
						]
					)
				],
				changes: shared({ 'f-gear-14': { checkout: other(MARIA, 2 * HOUR) }, 'f-gear-60': { checkout: other(MARIA, 2 * HOUR) } })
			})
		},

		projectPutBack: {
			label: 'A project folder renamed in File Explorer was put back',
			screens: ['home'],
			view: signedIn({
				sync: SYNCED,
				notices: [
					notice(
						'projectPutBack',
						'projectPutBack',
						'info',
						'The Robot 2027 folder was renamed back',
						'Project names are changed on ideabosco.com.',
						1,
						{ label: 'OK', command: 'dismissNotice', paths: [] },
						[]
					)
				],
				changes: shared({})
			})
		},

		projectRenaming: {
			label: 'A project renamed on the site, waiting for a file to close',
			screens: ['home'],
			view: signedIn({
				sync: SYNCED,
				notices: [
					notice(
						'projectRenaming',
						'projectRenaming',
						'info',
						'Close Plate.SLDPRT to finish renaming Robot 2027 to Robot 2028',
						'Your teacher renamed the project on ideabosco.com. Armory renames its folder on this computer as soon as nothing in it is open.',
						1,
						null,
						[]
					)
				],
				changes: shared({})
			})
		},

		archivedProject: {
			label: 'An archived project: listed, no longer kept up to date',
			screens: ['home'],
			params: { project: 'proj-robot-2026', at: 'browser' },
			view: signedIn({ sync: SYNCED, changes: shared({}), opts: { archived: true } })
		},

		selection: {
			label: 'Three files selected, with the selection bar',
			screens: ['home'],
			params: { folder: 'Drivetrain', select: 'Drivetrain.SLDDRW,Gearbox.SLDASM,Plate-Left.SLDPRT', at: 'browser' },
			view: signedIn({ sync: SYNCED, changes: shared({}) })
		},

		folderNew: {
			label: 'Making a new folder',
			screens: ['home'],
			params: { folder: 'Drivetrain', dialog: 'newFolder', at: 'browser' },
			view: signedIn({ sync: SYNCED, changes: shared({}) })
		},

		folderRename: {
			label: 'Renaming a folder',
			screens: ['home'],
			params: { folder: 'Drivetrain/Gearbox', dialog: 'renameFolder', at: 'browser' },
			view: signedIn({ sync: SYNCED, changes: shared({}) })
		},

		folderDelete: {
			label: 'Deleting a folder: how many files, and that history is kept',
			screens: ['home'],
			params: { folder: 'Drivetrain/Gearbox', dialog: 'deleteFolder', at: 'browser' },
			view: signedIn({ sync: SYNCED, changes: shared({}) })
		},

		dragOver: {
			label: 'Files dragged from File Explorer, held over the list',
			screens: ['home'],
			params: { folder: 'Intake', drag: '1', at: 'browser' },
			view: signedIn({ sync: SYNCED, changes: shared({}) })
		},

		takeBack: {
			label: 'As a mentor: take back a file someone else has checked out',
			screens: ['home', 'detail'],
			detailFileId: 'f-plate-left',
			params: { folder: 'Drivetrain', select: 'Plate-Left.SLDPRT', at: 'browser' },
			view: signedIn({ sync: SYNCED, changes: shared({ 'f-plate-left': MARIA_HAS_PLATE }), opts: { lead: true } })
		},

		takeBackConfirm: {
			label: 'As a mentor, taking a file back: what happens to the changes not checked in',
			screens: ['detail'],
			detailFileId: 'f-plate-left',
			params: { dialog: 'takeBack' },
			view: signedIn({ sync: SYNCED, changes: shared({ 'f-plate-left': MARIA_HAS_PLATE }), opts: { lead: true } })
		},

		bigProject: {
			label: 'A folder of 5,000 files, drawn a screenful at a time',
			screens: ['home'],
			params: { folder: 'CopyDesignTemp', at: 'browser' },
			view: signedIn({
				sync: SYNCED,
				changes: shared({
					'f-cdt-0007': { checkout: other(MARIA, 3 * HOUR) },
					'f-cdt-0012': { checkout: mine(20 * MIN) },
					'f-cdt-4999': { checkout: other(SAM, 1 * DAY) }
				}),
				opts: { files: BIG, folders: { 'proj-robot-2027': ['CopyDesignTemp'] } }
			})
		}
	};

	function groupedView() {
		return signedIn({
			sync: { state: 'attention', line: 'Everything else is saved. A few files need you.', detail: 'Last checked just now.', pendingCount: 2 },
			notices: [
				nameShared('CopyDesignTemp', 14),
				notice(
					'keptCopy',
					'keptCopy',
					'look',
					'Your changes to 3 files were kept as your own copies',
					'Someone else checked these in first, so your changes were kept in each file\'s history. Nothing was lost. Ask your CAD lead which one to keep.',
					3,
					{ label: 'OK', command: 'dismissNotice', paths: [] },
					[
						item('f-plate-left', 'Robot 2027/Drivetrain/Plate-Left.SLDPRT', 'Maria Lopez checked in first.'),
						item('f-wheel-hub', 'Robot 2027/Drivetrain/Wheel-Hub.SLDPRT', 'Alex Kim checked in first.'),
						item('f-bracket', 'Robot 2027/Intake/Bracket.SLDPRT', 'Alex Kim checked in first.')
					]
				),
				notice(
					'cantSend',
					'cantSend',
					'bad',
					"2 files can't be uploaded",
					'They were saved in SolidWorks 2026, and the team uses 2025. In SolidWorks, use Save As and pick 2025, then they upload by themselves.',
					2,
					null,
					[
						item('f-roller-shaft', 'Robot 2027/Intake/Roller-Shaft.SLDPRT', 'Saved in SolidWorks 2026.'),
						item('f-carriage', 'Robot 2027/Elevator/Carriage-Plate.SLDPRT', 'Saved in SolidWorks 2026.')
					]
				),
				notice(
					'newerWaiting',
					'newerWaiting',
					'look',
					'A newer Gearbox.SLDASM is waiting',
					'Close Gearbox.SLDASM in SolidWorks to get it. Your copy stays as it is until then.',
					1,
					{ label: 'Open it', command: 'launchFile', paths: ['Robot 2027/Drivetrain/Gearbox.SLDASM'] },
					[item('f-gearbox', 'Robot 2027/Drivetrain/Gearbox.SLDASM', null)]
				)
			],
			changes: shared({
				'f-plate-left': { status: 'keptCopy', updatedAt: ago(1 * HOUR) },
				'f-wheel-hub': { status: 'keptCopy' },
				'f-bracket': { status: 'keptCopy' },
				'f-gearbox': { checkout: available(), status: 'newerWaiting', updatedBy: MARIA.name, updatedAt: ago(4 * MIN) }
			})
		});
	}

	// The kept-copy file's history, with a removal and a revival in it.
	states.groupedNotices.details = {
		'f-plate-left': function (d) {
			d.history.splice(1, 0, {
				id: 'f-plate-left-k1',
				kind: 'keptCopy',
				author: ME.name,
				at: ago(55 * MIN),
				bytes: 618004,
				note: 'Kept as your own copy: Maria Lopez checked in first',
				releaseNotChecked: false,
				isCurrent: false,
				routine: false
			});
			d.history.splice(4, 0, {
				id: 'f-plate-left-gone',
				kind: 'removed',
				author: ALEX.name,
				at: ago(4 * DAY),
				bytes: 0,
				note: 'Removed from Robot 2027',
				releaseNotChecked: false,
				isCurrent: false,
				routine: false
			});
			d.history[3].note = 'Added again, with its history';
			return d;
		}
	};

	/* ------------------------------------------------------------- Details */

	/** A plain history: the current version, then older check ins by the team. */
	function history(entry, row) {
		var authors = [entry[4], MARIA, ALEX, ME, ALEX];
		var steps = [0, 1 * DAY, 2 * DAY + 3 * HOUR, 6 * DAY, 9 * DAY];
		return steps.map(function (step, i) {
			return {
				id: entry[0] + '-v' + (steps.length - i),
				kind: 'version',
				author: i === 0 && row && row.updatedBy ? row.updatedBy : authors[i].name,
				at: i === 0 && row && row.updatedAt ? row.updatedAt : ago(entry[5] + step),
				bytes: Math.round(entry[6] * (1 - i * 0.04)),
				note: i === steps.length - 1 ? 'Added to Armory' : 'Checked in',
				releaseNotChecked: i === 0 && !!(row && row.releaseNotChecked),
				isCurrent: i === 0,
				routine: false
			};
		});
	}

	/** The detail for a file, built from the row in the view the page has now. */
	function detailFor(stateName, fileId, currentView) {
		var s = states[stateName];
		var view = currentView || (s ? s.view : null);
		var found = null;
		if (view) {
			view.projects.forEach(function (p) {
				p.folders.forEach(function (f) {
					f.files.forEach(function (r) {
						if (r.fileId === fileId) found = { row: r, project: p, folder: f };
					});
				});
			});
		}
		var entry = catalogEntry(fileId);
		var row = found ? found.row : fileRow(entry, null);
		var d = {
			fileId: fileId,
			name: row.name,
			path: row.path,
			project: found ? found.project.name : projectOf(entry[1]).name,
			folder: found ? found.folder.path : entry[2],
			status: row.status,
			checkout: row.checkout,
			releaseNotChecked: row.releaseNotChecked,
			canTakeBack: found ? found.project.canTakeBack : false,
			history: history(entry, row)
		};
		if (s && s.details && s.details[fileId]) d = s.details[fileId](d);
		return d;
	}

	window.ArmoryDemoStates = {
		now: NOW,
		defaultState: 'synced',
		/** The state the demo starts in after "Connect this computer" finishes. */
		afterConnect: 'synced',
		states: states,
		detailFor: detailFor,
		pausedSync: pausedSync,
		checkoutMine: function () {
			return mine(0);
		},
		checkoutAvailable: available,
		/** Where a demo "Change" folder picker lands. */
		pickedVaultRoot: 'D:\\School\\Armory'
	};
})();
