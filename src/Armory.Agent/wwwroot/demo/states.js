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
	// The first n of BIG checked out here (everything checked out at once).
	function allMine(n) {
		var changes = {};
		for (var i = 1; i <= n; i++) changes['f-cdt-' + pad4(i)] = { checkout: mine(20 * MIN) };
		return changes;
	}
	// An unzip of 5,000 files: 4,987 were added, 13 share a name with a file the project has.
	var IMPORTED = generated(4987, 'CopyDesignTemp', 'f-imp');
	// A new arm on its way to this computer: 1,276 files, with four of the team's files
	// that changed, make the 1,280 being downloaded.
	var ARM = generated(1276, 'Arm', 'f-arm');
	// The shooter: 14 files, for checking out a whole folder.
	var SHOOTER = [
		'Shooter.SLDASM', 'Flywheel.SLDPRT', 'Flywheel-Shaft.SLDPRT', 'Hood.SLDPRT', 'Hood-Arc.SLDPRT', 'Side-Plate-Left.SLDPRT', 'Side-Plate-Right.SLDPRT',
		'Feeder-Roller.SLDPRT', 'Belt-Pulley-24T.SLDPRT', 'Belt-Pulley-36T.SLDPRT', 'Standoff-2in.SLDPRT', 'Motor-Plate.SLDPRT', 'Bearing-Block.SLDPRT', 'Shooter.SLDDRW'
	].map(function (name, i) {
		return ['f-sh-' + pad4(i + 1), 'proj-robot-2027', 'Shooter', name, i % 2 ? SAM : ALEX, 2 * DAY + i * 30, 90000 + i * 41000];
	});
	var EXTRA = {};
	BIG.concat(IMPORTED, ARM, SHOOTER).forEach(function (e) {
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
			updatedBy: entry[4].name,
			savedRelease: /\.sld(prt|asm|drw)$/i.test(entry[3]) ? 2025 : null,
			newerThanPin: false
		};
		if (change) for (var k in change) row[k] = change[k];
		return row;
	}

	/**
	 * Projects and folders as the server and this computer see them. `opts`:
	 *   files       extra catalog entries (a Pack and Go folder)
	 *   folders     extra folder paths, by project id
	 *   local       files in a folder that aren't in Armory: [projectId, folder, name, status]
	 *               (status notInArmory, the default, or waiting for a new one not sent yet)
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
			var project = {
				id: p.id,
				name: p.name,
				archived: p === ARCHIVED,
				role: lead ? 'mentor' : p.role,
				canTakeBack: lead,
				pinnedRelease: 2025,
				newerThanPinCount: 0,
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
								status: l[3] || 'notInArmory',
								checkout: available(),
								changed: false,
								releaseNotChecked: false,
								updatedAt: null,
								updatedBy: null,
								savedRelease: null,
								newerThanPin: false
							});
					});
					return { path: folder, name: folder ? folder.split('/').pop() : p.name, fileCount: files.length, files: files };
				})
			};
			project.folders.forEach(function (f) {
				f.files.forEach(function (r) {
					if (r.newerThanPin) project.newerThanPinCount++;
				});
			});
			return project;
		});
	}

	/** My files: the files this computer has checked out, in every project (an archived one
	 *  too, so they can always be checked in). Files that aren't in Armory are never here:
	 *  the notices and the folder's own rows say what became of them. */
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
		return { line: null, upload: null, download: null, move: null, waiting: null, active: [], log: [] };
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
			return item(null, 'Robot 2027/' + folder + '/' + s[0], 'The other one is in Robot 2027 \u203a ' + s[1].split('/').join(' \u203a ') + '.');
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

	/** The question when SolidWorks opens a file this computer hasn't checked out. Its key
	 *  is one per open: the path and when SolidWorks opened it. */
	function promptFor(fileId, minutesAgo, checkout, canCheckOut) {
		var e = catalogEntry(fileId);
		var path = pathOf(e);
		return { key: 'prompt:' + path + ':' + ago(minutesAgo), fileId: fileId, path: path, name: e[3], checkout: checkout, canCheckOut: canCheckOut };
	}

	/* ------------------------------------------------------------- Views */

	var SETTINGS = { vaultRoot: 'C:\\IDEA\\Armory', startAtSignIn: true, theme: 'system' };
	// Armory's status on file icons (the badges): not installed on this computer yet.
	function badgesOff() {
		return { state: 'off', line: 'Armory\'s status isn\'t shown on file icons on this computer. Turning it on needs an administrator once.' };
	}

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
			settings: { vaultRoot: SETTINGS.vaultRoot, startAtSignIn: SETTINGS.startAtSignIn, theme: SETTINGS.theme, badges: badgesOff(), sharedComputer: false },
			effectiveTheme: 'idea',
			folderOwner: null,
			profiles: null,
			solidWorks: null
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
			settings: { vaultRoot: SETTINGS.vaultRoot, startAtSignIn: SETTINGS.startAtSignIn, theme: SETTINGS.theme, badges: badgesOff(), sharedComputer: false },
			effectiveTheme: 'idea',
			folderOwner: null,
			profiles: null,
			solidWorks: null
		};
	}

	var SYNCED = { state: 'synced', line: 'Everything is saved to Armory.', detail: 'Last checked 2 minutes ago.', pendingCount: 0 };

	function pausedSync(pending) {
		return {
			state: 'paused',
			line: 'Paused. Nothing uploads or downloads until you resume.',
			// How many wait is Right now's to say (activity.waiting), once.
			detail: null,
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

	// What is moving: 1,280 files coming down (412 here already), three going up (one
	// done), and a folder's files moving. Every number on the screen agrees with these.
	var UPLOAD_DONE = 1 * MB; // the one file already uploaded
	var TRANSFERS = {
		line: 'Downloading 412 of 1,280 files, 2.1 GB left, about 3 min',
		download: direction(412, 1280, Math.round(1.4 * GB), Math.round(3.5 * GB), Math.round(12.6 * MB), 170, 'Downloading 412 of 1,280 files, 2.1 GB left, about 3 min'),
		// One file of three done: too soon to say how long (secondsLeft is null).
		upload: direction(1, 3, UPLOAD_DONE + 1210000 + 120000, UPLOAD_DONE + 1482113 + 951300, 225000, null, 'Uploading 1 of 3 files, 1.1 MB left'),
		move: direction(45, 120, 0, 0, 0, null, 'Moving 120 files to Gearbox'),
		waiting: null,
		active: [
			active('f-elevator-asm', 'download', Math.round(1.9 * MB), 2650177),
			active('f-stage-tube', 'download', 41000, 141876),
			active('f-carriage', 'download', 402000, 455030),
			active('f-intake-asm', 'download', 260000, 2210004),
			active('f-gearbox', 'upload', 1210000, 1482113),
			active('f-dt-drawing', 'upload', 120000, 951300)
		],
		// What Armory did in the last few minutes, the newest last.
		log: [
			{ at: ago(3), line: 'Downloaded Arm-Pivot.SLDPRT (1.2 MB)' },
			{ at: ago(2), line: 'Downloaded Arm-Gusset-0408.SLDPRT (220 KB)' },
			{ at: ago(2), line: 'Uploaded Gearbox.SLDASM (1.4 MB)' },
			{ at: ago(1), line: 'Downloaded Arm-Gusset-0409.SLDPRT (218 KB)' },
			{ at: ago(1), line: 'Downloaded Arm-Gusset-0410.SLDPRT (221 KB)' },
			{ at: ago(0), line: 'Downloaded Arm-Spacer-0411.SLDPRT (64 KB)' }
		]
	};

	/** The view while files move: the arm's first 412 files are here, the rest and four of
	 *  the team's changed files are on their way, and two of mine are going up. */
	function transferringView() {
		var changes = {
			'f-gearbox': { checkout: mine(35 * MIN), status: 'uploading', changed: true },
			'f-dt-drawing': { checkout: mine(50 * MIN), status: 'uploading', changed: true },
			'f-elevator-asm': { status: 'downloading' },
			'f-stage-tube': { status: 'downloading' },
			'f-carriage': { status: 'downloading' },
			'f-intake-asm': { checkout: other(ALEX, 40 * MIN), status: 'downloading' }
		};
		ARM.slice(412).forEach(function (e) {
			changes[e[0]] = { status: 'notOnThisComputer' };
		});
		return signedIn({
			sync: { state: 'syncing', line: TRANSFERS.line, detail: 'You can keep working.', pendingCount: 2 },
			activity: TRANSFERS,
			changes: shared(changes),
			opts: { files: ARM, folders: { 'proj-robot-2027': ['Arm'] } }
		});
	}

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
				// The folder's real owner, from the folder itself (the engine fills it); the
				// screen names them, never connect.message.
				var v = notSignedIn('vaultOwnedByOther', 'idle', null);
				v.account = { email: ME.email, deviceName: ME.device };
				v.folderOwner = { email: ALEX.email, name: ALEX.name, waiting: ['1 file checked out'] };
				return v;
			})()
		},

		synced: {
			label: 'Everything up to date: the file browser at a project\'s top folder',
			screens: ['home', 'detail', 'settings'],
			detailFileId: 'f-wheel-hub',
			view: signedIn({ sync: SYNCED, changes: shared({}) })
		},

		badgesCrowded: {
			label: 'Settings: the badges are installed, but other apps\' badges come first',
			screens: ['settings'],
			view: (function () {
				var v = signedIn({ sync: SYNCED, changes: shared({}) });
				v.settings.badges = {
					state: 'crowded',
					line: 'Windows isn\'t showing Armory\'s badges because 12 badges from other apps come first (Dropbox, OneDrive). Windows shows only 11.'
				};
				return v;
			})()
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
					active: [],
					log: []
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
			label: 'Uploading, downloading and moving at once, each file with its own bar (detail: a file downloading)',
			screens: ['home', 'detail'],
			detailFileId: 'f-elevator-asm',
			view: transferringView()
		},

		notHereYet: {
			label: 'A file that is not on this computer yet, while the rest come down',
			screens: ['detail'],
			detailFileId: 'f-arm-0900',
			view: transferringView()
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
					active: [],
					log: []
				},
				// Three waiting: two of my check outs with saves, and a new file not sent yet.
				changes: shared({
					'f-gearbox': { checkout: mine(50 * MIN), status: 'waiting', changed: true },
					'f-plate-right': { checkout: mine(30 * MIN), status: 'waiting', changed: true }
				}),
				opts: { local: [['proj-robot-2027', 'Intake', 'Intake-Gearbox.SLDASM', 'waiting']] }
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
					active: [],
					log: []
				},
				changes: shared({
					'f-gearbox': { checkout: mine(50 * MIN), status: 'waiting', changed: true },
					'f-plate-right': { checkout: mine(30 * MIN), status: 'waiting', changed: true }
				})
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
						'Added 4,987 of 5,000 files to Robot 2027 \u203a CopyDesignTemp',
						'12 of them were brought back with their history. 13 need you: they share a name with other files in this project.',
						5000,
						{ label: 'Done', command: 'dismissNotice', paths: [] },
						[]
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
				prompt: promptFor('f-plate-left', 2, available(), true),
				changes: shared({})
			})
		},

		checkoutPromptTaken: {
			label: 'SolidWorks opened a file someone else has checked out',
			screens: ['home'],
			params: { folder: 'Drivetrain' },
			view: signedIn({
				sync: SYNCED,
				prompt: promptFor('f-plate-left', 2, other(MARIA, 25 * MIN), false),
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
			label: 'An archived project: listed, no longer kept up to date; my check out in it stays in My files',
			screens: ['home'],
			params: { project: 'proj-robot-2026' },
			view: signedIn({ sync: SYNCED, changes: shared({ 'f-chassis-rail': { checkout: mine(41 * DAY) } }), opts: { archived: true } })
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
			label: 'As a mentor: force check in a file someone else has checked out (on its row, its page and the folder keys)',
			screens: ['home', 'detail'],
			detailFileId: 'f-plate-left',
			params: { folder: 'Drivetrain', select: 'Plate-Left.SLDPRT', at: 'browser' },
			view: signedIn({ sync: SYNCED, changes: shared({ 'f-plate-left': MARIA_HAS_PLATE }), opts: { lead: true } })
		},

		takeBackConfirm: {
			label: 'As a mentor, forcing a check in: who has it, and what happens to the changes not checked in',
			screens: ['detail'],
			detailFileId: 'f-plate-left',
			params: { dialog: 'takeBack' },
			view: signedIn({ sync: SYNCED, changes: shared({ 'f-plate-left': MARIA_HAS_PLATE }), opts: { lead: true } })
		},

		forceAllConfirm: {
			label: 'As a mentor, Force check in all for a folder: whose files, and that their changes are kept as their own copies',
			screens: ['home'],
			params: { folder: 'Drivetrain', at: 'browser', dialog: 'forceAll' },
			view: signedIn({ sync: SYNCED, changes: shared({ 'f-plate-left': MARIA_HAS_PLATE }), opts: { lead: true } })
		},

		working: {
			label: 'Just pressed Check out: the key turns, its row says Checking out, and the line at the foot says what is under way',
			screens: ['home'],
			params: { folder: 'Drivetrain', at: 'browser', press: 'state-f-plate-right' },
			view: signedIn({ sync: SYNCED, changes: shared({}) })
		},

		renameFile: {
			label: 'Renaming a file that shares a name, from its notice, in the app',
			screens: ['home'],
			params: { expand: 'nameShared', dialog: 'renameFile' },
			view: groupedView()
		},

		checkOutAll: {
			label: 'Check out all asks first: how many files, and that nobody else can save them',
			screens: ['home'],
			params: { folder: 'Intake', dialog: 'checkOutAll', at: 'browser' },
			view: signedIn({ sync: SYNCED, changes: shared({}) })
		},

		reportProblem: {
			label: 'Report a problem (from Settings): what kind, the words, and that only file names go with them',
			screens: ['home'],
			params: { dialog: 'report' },
			view: signedIn({ sync: SYNCED, changes: shared({}) })
		},

		sendFeedback: {
			label: 'Send feedback, the same as the website\'s: four kinds, the words, what was tried, what it is about, and a picture of this window to add',
			screens: ['home'],
			params: { folder: 'Drivetrain', at: 'browser', dialog: 'feedback' },
			view: signedIn({ sync: SYNCED, changes: shared({}) })
		},

		feedbackPicture: {
			label: 'Send feedback with a picture of this window: exactly what would go, its size, and a key to remove it',
			screens: ['home'],
			params: { folder: 'Drivetrain', at: 'browser', dialog: 'feedback', words: 'Check in spun for a minute on Gearbox.SLDASM, then said it was done.', shot: '1' },
			view: signedIn({ sync: SYNCED, changes: shared({}) })
		},

		feedbackWithoutPicture: {
			label: 'A picture that couldn\'t go: the words stay in the dialog, and the note can go without it',
			screens: ['home'],
			params: { folder: 'Drivetrain', at: 'browser', dialog: 'feedback', words: 'Check in spun for a minute on Gearbox.SLDASM, then said it was done.', shot: 'offer' },
			view: signedIn({ sync: SYNCED, changes: shared({}) })
		},

		yourFeedback: {
			label: 'Your feedback: each note, where it is with the IDEA team, and that there are no replies in Armory',
			screens: ['home'],
			params: { dialog: 'myFeedback' },
			view: signedIn({ sync: SYNCED, changes: shared({}) })
		},

		yourFeedbackHidden: {
			label: 'Settings while the website has no Your feedback yet: the key is not there',
			screens: ['settings'],
			params: { feedback: 'missing' },
			view: signedIn({ sync: SYNCED, changes: shared({}) })
		},

		partialCheckOut: {
			label: 'A folder checked out, two of its files held by someone else: the answer at the foot',
			screens: ['home'],
			params: { folder: 'Shooter', at: 'browser', result: 'Checked out 12 of 14 files. Maria Lopez has 2 of them checked out.' },
			view: (function () {
				var changes = {};
				SHOOTER.forEach(function (e) {
					changes[e[0]] = { checkout: e[3] === 'Hood.SLDPRT' || e[3] === 'Hood-Arc.SLDPRT' ? other(MARIA, 3 * HOUR) : mine(0) };
				});
				return signedIn({ sync: SYNCED, changes: shared(changes), opts: { files: SHOOTER, folders: { 'proj-robot-2027': ['Shooter'] } } });
			})()
		},

		myOtherComputer: {
			label: 'A file checked out on my other computer: amber, and how to get it here',
			screens: ['home', 'detail'],
			detailFileId: 'f-wheel-hub',
			params: { folder: 'Drivetrain', at: 'browser' },
			view: signedIn({ sync: SYNCED, changes: shared({ 'f-wheel-hub': { checkout: myOther(2 * HOUR) } }) })
		},

		emptyFolder: {
			label: 'An empty folder',
			screens: ['home'],
			params: { folder: 'Intake/Rollers', at: 'browser' },
			view: signedIn({ sync: SYNCED, changes: shared({}), opts: { folders: { 'proj-robot-2027': ['Intake/Rollers'] } } })
		},

		moreNotices: {
			label: 'The other notices: a file taken back, files Armory can\'t read, a check in that left two out',
			screens: ['home'],
			view: signedIn({
				sync: { state: 'attention', line: 'Everything else is saved. A few files need you.', detail: 'Last checked just now.', pendingCount: 0 },
				notices: [
					notice(
						'takenBack',
						'takenBack',
						'look',
						'Mr. Pina took back Plate-Left.SLDPRT',
						'Your changes that weren\'t checked in are kept in its history, so nothing was lost. Check it out again to keep working on it.',
						1,
						null,
						[item('f-plate-left', 'Robot 2027/Drivetrain/Plate-Left.SLDPRT', null)]
					),
					notice(
						'checkInPartial',
						'checkInPartial',
						'look',
						"2 files weren't checked in",
						'They are still open in SolidWorks. Close them there, then check them in.',
						2,
						{ label: 'Check them in', command: 'checkIn', paths: ['Robot 2027/Drivetrain/Gearbox.SLDASM', 'Robot 2027/Elevator/Carriage-Plate.SLDPRT'] },
						[
							item('f-gearbox', 'Robot 2027/Drivetrain/Gearbox.SLDASM', 'Open in SolidWorks.'),
							item('f-carriage', 'Robot 2027/Elevator/Carriage-Plate.SLDPRT', 'Open in SolidWorks.')
						]
					),
					notice(
						'cantRead',
						'cantRead',
						'bad',
						"Armory can't read 2 files",
						'Another program is holding them, so Armory can\'t see your changes. Close that program, and Armory tries again by itself.',
						2,
						null,
						[
							item('f-motor', 'Robot 2027/COTS/Motor-Mount.SLDPRT', 'Another program has it open.'),
							item('f-collar', 'Robot 2027/COTS/Shaft-Collar.SLDPRT', 'Another program has it open.')
						]
					)
				],
				changes: shared({
					'f-plate-left': { status: 'keptCopy', updatedBy: PINA.name, updatedAt: ago(10 * MIN) },
					'f-gearbox': { checkout: mine(35 * MIN), changed: true, status: 'changed' },
					'f-carriage': { checkout: mine(3 * HOUR), changed: true, status: 'changed' }
				})
			})
		},

		manyMine: {
			label: 'Everything checked out: 1,400 files in My files, in a box of their own with Check in all on top, so Team files stays right under it',
			screens: ['home'],
			params: {},
			view: signedIn({ sync: SYNCED, changes: shared(allMine(1400)), opts: { files: BIG, folders: { 'proj-robot-2027': ['CopyDesignTemp'] } } })
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
					'They were saved in SolidWorks 2026, and the team uses 2025. Each stays on this computer until it is saved in 2025, then it uploads by itself.',
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
					null,
					[item('f-gearbox', 'Robot 2027/Drivetrain/Gearbox.SLDASM', null)]
				)
			],
			changes: shared({
				'f-plate-left': { status: 'keptCopy', updatedAt: ago(1 * HOUR) },
				'f-wheel-hub': { status: 'keptCopy' },
				'f-bracket': { status: 'keptCopy' },
				'f-gearbox': { checkout: available(), status: 'newerWaiting', updatedBy: MARIA.name, updatedAt: ago(4 * MIN) }
			}),
			// The 14 files from the unzip that share a name are in their folder, not in Armory.
			opts: {
				folders: { 'proj-robot-2027': ['CopyDesignTemp'] },
				local: SHARED_NAMES.map(function (s) {
					return ['proj-robot-2027', 'CopyDesignTemp', s[0]];
				})
			}
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
				note: 'Kept as ' + ME.name + '\'s own copy: someone else checked in first',
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

	/* ------------------------------------------------- A shared computer */

	// Several students taking turns on one lab computer (docs/agent/PROFILES.md): each has a
	// profile with their own sign-in and a 4-digit PIN. While the picker shows, the view
	// carries none of the student in use's files (the host empties them), so these views
	// are the signed-in one with every file left out.
	var PROFILE = {
		me: { id: '6f1c0e2a9b7d4c3e8a5f1b2c3d4e5f60', hue: 3, person: ME },
		maria: { id: '1a2b3c4d5e6f708192a3b4c5d6e7f801', hue: 5, person: MARIA },
		alex: { id: '2b3c4d5e6f708192a3b4c5d6e7f80912', hue: 1, person: ALEX },
		sam: { id: '3c4d5e6f708192a3b4c5d6e7f8091a23', hue: 6, person: SAM },
		pina: { id: '4d5e6f708192a3b4c5d6e7f8091a2b34', hue: 2, person: PINA }
	};
	var OWN_FOLDER = 'C:\\IDEA\\Armory-jordan';

	function initialsOf(name) {
		var words = String(name).replace(/^(Mr|Mrs|Ms|Dr|Mx)\.?\s+/i, '').split(/\s+/);
		return (words[0].charAt(0) + (words.length > 1 ? words[words.length - 1].charAt(0) : '')).toUpperCase();
	}

	/** One student's tile, as the host sends it. */
	function profileTile(key, o) {
		var p = PROFILE[key];
		o = o || {};
		return {
			id: p.id,
			name: p.person.name,
			email: p.person.email,
			initials: initialsOf(p.person.name),
			hue: p.hue,
			current: !!o.current,
			lastUsedAt: o.lastUsedAt === undefined ? ago(o.current ? 25 * MIN : 1 * DAY) : o.lastUsedAt,
			folder: o.folder || SETTINGS.vaultRoot,
			ownFolder: !!o.ownFolder,
			waiting: o.waiting || null,
			needsSignIn: !!o.needsSignIn,
			hasPin: o.hasPin !== false,
			canRemove: !!o.canRemove
		};
	}

	function pickerStep(kind, o) {
		var s = { kind: kind, profileId: null, message: null, triesLeft: null, waitSeconds: null, ownFolder: null, ownerName: null, ownerWaiting: null, fromName: null, connectPhase: null };
		for (var k in o || {}) s[k] = o[k];
		return s;
	}

	/** The students on this computer, the picker and its step. */
	function profilesOf(o) {
		var tiles = o.tiles;
		var current = tiles.filter(function (t) {
			return t.current;
		})[0];
		return {
			showing: !!o.showing,
			currentId: current ? current.id : null,
			sharedFolder: SETTINGS.vaultRoot,
			pinsRequired: o.pinsRequired !== false,
			canChangePins: !!o.canChangePins,
			pinsNote: o.pinsNote || null,
			canTurnOff: !!o.canTurnOff,
			note: o.note || null,
			profiles: tiles,
			step: o.step || pickerStep('choose')
		};
	}

	/** The usual tiles: Jordan in use, then Maria, Alex and Sam. */
	function labTiles(o) {
		o = o || {};
		return [
			profileTile('me', { current: !o.nobody, canRemove: !o.nobody }),
			profileTile('maria', { lastUsedAt: ago(2 * HOUR), needsSignIn: !!o.mariaSignIn }),
			profileTile('alex', { lastUsedAt: ago(1 * DAY), waiting: o.alexWaiting || null }),
			profileTile('sam', { lastUsedAt: ago(3 * DAY) })
		];
	}

	/** A view while the picker shows: the student in use keeps working (the status says so),
	 *  and nothing of their files is in it. */
	function picking(profiles, nobody) {
		var v = nobody ? notSignedIn('signedOut', 'idle', null) : notSignedIn('signedIn', 'idle', null);
		if (!nobody) v.sync = { state: 'synced', line: 'Everything is saved to Armory.', detail: 'Last checked 2 minutes ago.', pendingCount: 0 };
		v.settings.sharedComputer = true;
		v.profiles = profiles;
		return v;
	}

	/** The signed-in view of a student in use on a shared computer. */
	function sharedHome(view, profiles) {
		view.settings.sharedComputer = true;
		view.profiles = profiles;
		return view;
	}

	var ALEX_WAITS = '2 files checked out';

	states.pickerChoose = {
		label: 'Shared computer: who is using Armory? Jordan is in use; Maria, Alex and Sam can pick themselves',
		screens: ['picker'],
		view: picking(profilesOf({ showing: true, tiles: labTiles() }))
	};

	states.pickerWaiting = {
		label: 'Shared computer: Alex is in use, and his tile says what waits for him',
		screens: ['picker'],
		view: picking(
			profilesOf({
				showing: true,
				tiles: [
					profileTile('alex', { current: true, canRemove: true }),
					profileTile('me', { lastUsedAt: ago(3 * HOUR) }),
					profileTile('maria', { lastUsedAt: ago(1 * DAY), waiting: '1 file checked out', folder: 'C:\\IDEA\\Armory-maria', ownFolder: true }),
					profileTile('sam', { lastUsedAt: ago(3 * DAY) })
				]
			})
		)
	};

	states.pickerPin = {
		label: 'Shared computer: Maria picked herself and types her PIN',
		screens: ['picker'],
		view: picking(profilesOf({ showing: true, tiles: labTiles(), step: pickerStep('pin', { profileId: PROFILE.maria.id, triesLeft: 5 }) }))
	};

	states.pickerPinWrong = {
		label: "Shared computer: a PIN that isn't right, and the tries left",
		screens: ['picker'],
		view: picking(
			profilesOf({
				showing: true,
				tiles: labTiles(),
				step: pickerStep('pin', { profileId: PROFILE.maria.id, triesLeft: 2, message: "That PIN isn't right. 2 more tries, then a short wait." })
			})
		)
	};

	states.pickerPinWait = {
		label: 'Shared computer: five wrong PINs, a 30 second wait, and Forgot your PIN',
		screens: ['picker'],
		view: picking(
			profilesOf({
				showing: true,
				tiles: labTiles(),
				step: pickerStep('pin', {
					profileId: PROFILE.maria.id,
					triesLeft: 0,
					waitSeconds: 30,
					message: 'Too many wrong tries. Try again in 30 seconds, or sign in with Google instead.'
				})
			})
		)
	};

	states.pickerAdding = {
		label: 'Shared computer: adding a student, waiting for Google, with "Not you?" the step to look at',
		screens: ['picker'],
		view: picking(profilesOf({ showing: true, tiles: labTiles(), step: pickerStep('adding', { connectPhase: 'waitingForBrowser' }) }))
	};

	states.pickerNewPin = {
		label: 'Shared computer: Sam was just added and chooses a PIN',
		screens: ['picker'],
		view: picking(profilesOf({ showing: true, tiles: labTiles(), step: pickerStep('newPin', { profileId: PROFILE.sam.id }) }))
	};

	states.pickerFolderBusy = {
		label: "Shared computer: Jordan picked himself; Alex's 2 check outs are in the folder: wait, or a folder of his own",
		screens: ['picker'],
		view: picking(
			profilesOf({
				showing: true,
				tiles: [
					profileTile('alex', { current: true }),
					profileTile('me', { lastUsedAt: ago(3 * HOUR) }),
					profileTile('maria', { lastUsedAt: ago(1 * DAY) }),
					profileTile('sam', { lastUsedAt: ago(3 * DAY) })
				],
				step: pickerStep('folderBusy', { profileId: PROFILE.me.id, ownFolder: OWN_FOLDER, ownerName: ALEX.name, ownerWaiting: ALEX_WAITS })
			})
		)
	};

	states.pickerSwitching = {
		label: 'Shared computer: switching from Alex to Jordan',
		screens: ['picker'],
		view: picking(
			profilesOf({
				showing: true,
				tiles: [profileTile('alex', { current: true }), profileTile('me', { lastUsedAt: ago(3 * HOUR) }), profileTile('maria'), profileTile('sam')],
				step: pickerStep('switching', { profileId: PROFILE.me.id, fromName: ALEX.name })
			})
		)
	};

	states.pickerSignInAgain = {
		label: "Shared computer: Maria's sign-in ended; her tile and her Sign in again",
		screens: ['picker'],
		view: picking(profilesOf({ showing: true, tiles: labTiles({ mariaSignIn: true }), step: pickerStep('signInAgain', {
			profileId: PROFILE.maria.id,
			message: 'Your sign-in on this computer ended. Sign in with your school Google account once more.'
		}) }))
	};

	states.pickerFirst = {
		label: 'Shared computer: just turned on, nobody yet, only Add a student',
		screens: ['picker'],
		view: picking(profilesOf({ showing: true, tiles: [], canTurnOff: true }), true)
	};

	states.pickerPinsOff = {
		label: 'Shared computer: PINs turned off by Mr. Pina, one click switches',
		screens: ['picker'],
		view: picking(profilesOf({ showing: true, pinsRequired: false, pinsNote: 'Turned off by Mr. Pina on Oct 1.', canTurnOff: true, tiles: labTiles() }))
	};

	states.sharedHome = {
		label: 'Shared computer: Home for Jordan, the student in use, with Switch student',
		screens: ['home'],
		view: sharedHome(signedIn({ sync: SYNCED, changes: shared({}) }), profilesOf({ tiles: labTiles() }))
	};

	states.sharedOwnFolder = {
		label: "Shared computer: Jordan in a folder of his own while Alex's work waits in the shared one",
		screens: ['home'],
		view: (function () {
			var v = sharedHome(
				signedIn({ sync: SYNCED, changes: shared({}) }),
				profilesOf({
					tiles: [
						profileTile('me', { current: true, folder: OWN_FOLDER, ownFolder: true, canRemove: true }),
						profileTile('alex', { lastUsedAt: ago(40 * MIN), waiting: ALEX_WAITS }),
						profileTile('maria'),
						profileTile('sam')
					],
					note: "You're in your own folder, " + OWN_FOLDER + ", while Alex's work waits in " + SETTINGS.vaultRoot + '.'
				})
			);
			v.vaultRoot = OWN_FOLDER;
			return v;
		})()
	};

	states.sharedSettings = {
		label: 'Shared computer: Settings for a student (Remove only on themselves, the PIN switch is a mentor\'s)',
		screens: ['settings'],
		view: sharedHome(signedIn({ sync: SYNCED, changes: shared({}) }), profilesOf({ tiles: labTiles({ alexWaiting: ALEX_WAITS }) }))
	};

	states.sharedSettingsMentor = {
		label: 'Shared computer: Settings for Mr. Pina, a mentor: the PIN switch and Remove on every student',
		screens: ['settings'],
		view: (function () {
			var v = sharedHome(
				signedIn({ sync: SYNCED, changes: shared({}) }),
				profilesOf({
					canChangePins: true,
					canTurnOff: true,
					tiles: [
						profileTile('pina', { current: true, canRemove: true }),
						profileTile('me', { lastUsedAt: ago(3 * HOUR), canRemove: true }),
						profileTile('maria', { lastUsedAt: ago(1 * DAY), canRemove: true }),
						profileTile('alex', { lastUsedAt: ago(1 * DAY), waiting: ALEX_WAITS, canRemove: true })
					]
				})
			);
			v.account = { email: PINA.email, deviceName: ME.device };
			return v;
		})()
	};

	// What the demo does for the picker's messages (bridge.js answers them from here): the view
	// is the shared Home of whoever is picked; the right PIN for everyone is 2580.
	var DEMO_PIN = '2580';
	var sharedDemo = {
		/** The picker over the view in use (its files left out). */
		picking: function (v) {
			if (!v.profiles) return v;
			var p = JSON.parse(JSON.stringify(v.profiles));
			p.showing = true;
			p.step = pickerStep('choose');
			return picking(p, !p.currentId);
		},
		step: function (v, kind, o) {
			var out = sharedDemo.picking(v);
			out.profiles.step = pickerStep(kind, o);
			return out;
		},
		/** The picked student in use: their Home. */
		use: function (v, id, folder) {
			var p = JSON.parse(JSON.stringify(v.profiles));
			p.profiles.forEach(function (t) {
				t.current = t.id === id;
				if (t.current) {
					t.lastUsedAt = new Date(Date.parse(NOW)).toISOString();
					if (folder) {
						t.folder = folder;
						t.ownFolder = folder !== SETTINGS.vaultRoot;
					}
					t.waiting = null;
					t.canRemove = true;
				}
			});
			p.currentId = id;
			p.showing = false;
			p.step = pickerStep('choose');
			var who = p.profiles.filter(function (t) {
				return t.current;
			})[0];
			var home = sharedHome(signedIn({ sync: SYNCED, changes: shared({}) }), p);
			home.account = { email: who.email, deviceName: ME.device };
			if (folder && folder !== SETTINGS.vaultRoot) home.vaultRoot = folder;
			return home;
		},
		pick: function (v, id) {
			var tile = v.profiles.profiles.filter(function (t) {
				return t.id === id;
			})[0];
			if (tile && tile.needsSignIn)
				return sharedDemo.step(v, 'signInAgain', { profileId: id, message: 'Your sign-in on this computer ended. Sign in with your school Google account once more.' });
			if (!v.profiles.pinsRequired) return sharedDemo.use(v, id);
			return sharedDemo.step(v, 'pin', { profileId: id, triesLeft: 5 });
		},
		enterPin: function (v, id, pin) {
			if (pin === DEMO_PIN) return { ok: true, view: sharedDemo.use(v, id) };
			var left = Math.max(0, (v.profiles.step.triesLeft == null ? 5 : v.profiles.step.triesLeft) - 1);
			return {
				ok: false,
				view: sharedDemo.step(v, 'pin', left
					? { profileId: id, triesLeft: left, message: "That PIN isn't right. " + (left === 1 ? '1 more try' : left + ' more tries') + ', then a short wait.' }
					: { profileId: id, triesLeft: 0, waitSeconds: 30, message: 'Too many wrong tries. Try again in 30 seconds, or sign in with Google instead.' })
			};
		},
		/** A just-added student: Sam Patel, unless he is here already. */
		added: function (v) {
			var out = sharedDemo.step(v, 'newPin', { profileId: PROFILE.sam.id });
			if (!out.profiles.profiles.some(function (t) { return t.id === PROFILE.sam.id; })) out.profiles.profiles.push(profileTile('sam', { lastUsedAt: null }));
			return out;
		},
		remove: function (v, id) {
			var p = JSON.parse(JSON.stringify(v.profiles));
			var gone = p.profiles.filter(function (t) {
				return t.id === id;
			})[0];
			p.profiles = p.profiles.filter(function (t) {
				return t.id !== id;
			});
			v.profiles = p;
			if (gone && gone.current) return { name: gone.name, view: sharedDemo.step(v, 'choose') };
			return { name: gone ? gone.name : 'That student', view: v };
		},
		ownFolder: OWN_FOLDER,
		/** Shared mode just turned on: Jordan is the first student. */
		turnedOn: function (v) {
			return sharedHome(v, profilesOf({ canTurnOff: true, tiles: [profileTile('me', { current: true, canRemove: true })] }));
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
			history: history(entry, row),
			savedRelease: row.savedRelease,
			newerThanPin: row.newerThanPin
		};
		if (s && s.details && s.details[fileId]) d = s.details[fileId](d);
		return d;
	}

	/* ------------------------------------------------------ Send feedback */

	/** This account's notes, newest first, as readMyFeedback answers (FeedbackNoteView). */
	var MY_FEEDBACK = [
		{
			id: '0f8a2c1e-5b3d-4e6f-8a7b-9c0d1e2f3a41',
			createdAt: ago(2 * HOUR),
			kind: 'bug',
			body: 'Check in spun for a minute on Gearbox.SLDASM, then said it was done. It happened twice today.',
			tried: 'Closed SolidWorks and checked it in again.',
			area: 'Home > Robot 2027 > Drivetrain',
			hasScreenshot: true,
			appVersion: '0.3.3',
			deviceName: ME.device,
			status: 'new',
			statusWords: 'Not read yet',
			reviewedAt: null
		},
		{
			id: '0f8a2c1e-5b3d-4e6f-8a7b-9c0d1e2f3a40',
			createdAt: ago(DAY + 3 * HOUR),
			kind: 'idea',
			body: 'Show who else is working in a folder before I check out all of it.',
			tried: null,
			area: 'Home',
			hasScreenshot: false,
			appVersion: '0.3.2',
			deviceName: ME.device,
			status: 'seen',
			statusWords: 'Read by the IDEA team',
			reviewedAt: ago(20 * HOUR)
		},
		{
			id: '0f8a2c1e-5b3d-4e6f-8a7b-9c0d1e2f3a3f',
			createdAt: ago(5 * DAY),
			kind: 'praise',
			body: 'Check in all is fast now. Thank you!',
			tried: null,
			area: 'Settings',
			hasScreenshot: false,
			appVersion: '0.3.2',
			deviceName: 'ROOM-209-PC',
			status: 'resolved',
			statusWords: 'Done',
			reviewedAt: ago(4 * DAY)
		}
	];

	/** The demo's picture of the window: a small drawing of one (the app's is the window
	 *  itself), and what the app's picture of the window would weigh. */
	var WINDOW_SHOT = {
		id: '9f2c4e0a1b3d4c5e8f7a6b5c4d3e2f10',
		url:
			'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAHAAAABMCAIAAAA6OqTKAAABL0lEQVR42u3cMQrCQBAF0JxBxE4RbS0sbIQg2HgeS4/m8SwEEYtoMNnNzj74rWheMfOTVZvldi0DpkEAtBzQ2/36TMhLbS/nH9P3JUCBFgoqlhLQ8KCzxVwGDFCgQOsFTTm8c73v2NcCFCjQ30E7bumADgm6O+xHSveHPJyOHekHumo3vQK0JNBcATrhGZoFNPKWf8d6PXj+CFCglYHWMkOzg6YpSem2PNBooNF6qBlqyyv2QIG6lwf6X22a1pYP8IAZaOgeCtSpJ1CgQPuB5krYGTpZ0FKPkYHWAqqH6qG2PFCgFYMmWKl1zdCpgRZ/jAxUbdJD9VBbHmi4LQ80M6gemgjUMbJzebUJqNpky485toACLRDUDPV7eaBA9VAzVA8FCtT3Q8WfCQIFKt/yAPGX/Ws5O4PZAAAAAElFTkSuQmCC',
		bytes: 219113
	};

	window.ArmoryDemoStates = {
		now: NOW,
		/** Your feedback, and Send feedback's picture of the window. */
		myFeedback: MY_FEEDBACK,
		windowShot: WINDOW_SHOT,
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
		/** The signed-in student's name, for a file the demo adds. */
		me: ME.name,
		/** Where a demo "Change" folder picker lands. */
		pickedVaultRoot: 'D:\\School\\Armory',
		/** The shared computer's picker, for the demo transport. */
		shared: sharedDemo
	};
})();
