const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { test } = require("node:test");

const html = fs.readFileSync(path.join(__dirname, "../Pages/desk.html"), "utf8");
const script = html.match(/<script>([\s\S]+)<\/script>/)[1];
new vm.Script(script); // Check the complete embedded web page script parses.
const analysis = script.slice(script.indexOf("const DeskAnalysis"), script.indexOf("        const page = document"));
const desk = vm.runInNewContext(analysis + "\nDeskAnalysis");
const gib = 1024 ** 3;
const timestamp = offset => new Date(Date.UTC(2026, 10, 1 + offset)).toISOString();
const volume = available => ({ mount: "/mnt/media", fileSystem: "fuse.mergerfs", device: "0:72", total: 1000 * gib, available: available * gib });
const report = (offset, available) => ({ sampledAt: timestamp(offset), volumes: [volume(available)] });

test("live playback distinguishes paused sessions, remuxes, audio-only transcodes and video transcodes", () => {
    const item = { Name: "Example" };
    const streams = desk.livePlayback([
        { TranscodingInfo: { IsVideoDirect: false } },
        { NowPlayingItem: item, PlayState: { PlayMethod: "DirectPlay", IsPaused: true } },
        { NowPlayingItem: item, PlayState: { PlayMethod: "DirectStream" }, TranscodingInfo: { IsVideoDirect: true, IsAudioDirect: false, AudioCodec: "aac" } },
        { NowPlayingItem: item, TranscodingInfo: { IsVideoDirect: true, IsAudioDirect: true } },
        { NowPlayingItem: item, PlayState: { PlayMethod: "DirectStream" }, TranscodingInfo: { IsVideoDirect: false, VideoCodec: "h264" } },
        { NowPlayingItem: item, PlayState: { PlayMethod: "Transcode" }, TranscodingInfo: { IsVideoDirect: false } }
    ]);
    assert.equal(Array.from(streams, stream => stream.method).join(","), "Direct play,Audio transcode,Remux,Video transcode,Transcode");
    assert.equal(streams.filter(stream => stream.paused).length, 1);
    assert.equal(streams.filter(stream => stream.isTranscoding).length, 3);
    assert.equal(streams[1].acceleration, undefined);
    assert.equal(streams[3].acceleration, "Video acceleration unknown");
});

test("playback details use the selected media and audio source and show explicit acceleration evidence", () => {
    const item = { MediaSources: [{ Id: "other", Bitrate: 9000000 }, {
        Id: "selected", Bitrate: 4000000, MediaStreams: [
            { Type: "Video", Codec: "hevc" }, { Type: "Audio", Codec: "dts", Index: 1 },
            { Type: "Audio", Codec: "aac", Index: 2 }
        ]
    }] };
    const state = { MediaSourceId: "selected", AudioStreamIndex: 2 };
    const engine = { IsVideoDirect: false, VideoCodec: "h264", AudioCodec: "aac", Bitrate: 2000000,
        TranscodeReasons: ["VideoCodecNotSupported"] };
    const [direct, hardware, software, unknown] = desk.livePlayback([
        { NowPlayingItem: item, PlayState: { ...state, PlayMethod: "DirectPlay", IsPaused: false } },
        ...["qsv", "none", "unrecognized"].map(api => ({ NowPlayingItem: item, PlayState: state,
            TranscodingInfo: { ...engine, HardwareAccelerationType: api } }))
    ]);
    assert.equal(direct.videoCodec, "hevc");
    assert.equal(direct.audioCodec, "aac");
    assert.equal(direct.sourceBitrate, 4000000);
    assert.equal(direct.outputBitrate, null);
    assert.equal(direct.paused, false);
    assert.equal(hardware.acceleration, "Intel Quick Sync reported");
    assert.equal(hardware.outputBitrate, 2000000);
    assert.equal(hardware.reasons[0], "VideoCodecNotSupported");
    assert.equal(software.acceleration, "Software reported");
    assert.equal(unknown.acceleration, "Video acceleration unknown");
});

test("recent transcode rows show encoder evidence and only count matching error lines", () => {
    const hardware = desk.playbackLogSummary({ name: "FFmpeg.Transcode-phone.log",
        encoders: [{ encoder: "h264_qsv", acceleration: "Intel Quick Sync encoder" }], signalCount: 0 });
    assert.equal(hardware.title, "Transcode");
    assert.equal(hardware.encoder, "h264_qsv · Intel Quick Sync encoder");
    assert.equal(hardware.issue, null);
    const remux = desk.playbackLogSummary({ name: "FFmpeg.Remux-sample.log", encoders: [], signalCount: 2 });
    assert.equal(remux.title, "Remux");
    assert.equal(remux.encoder, "No video encoder identified");
    assert.equal(remux.issue, "2 matching error lines");
});

test("the raw log tail button works with Jellyfin Web's element creation shim", async () => {
    const createNode = () => ({
        children: [],
        append(...children) { this.children.push(...children); },
        setAttribute() {},
        addEventListener(_, handler) { this.click = handler; }
    });
    const requests = [];
    const context = {
        document: { createElement(tag, extension) {
            if (extension) extension.toLowerCase();
            return createNode();
        } },
        trendText: createNode,
        signal: new AbortController().signal,
        read: async (route, params) => {
            requests.push([route, params.name]);
            return { content: "first\rsecond\r\nthird", truncated: true };
        },
        problem: error => { throw error; }
    };
    const rawTailSource = script.slice(script.indexOf("        function rawPlaybackTail"), script.indexOf("        const arrProblems"));
    const rawPlaybackTail = vm.runInNewContext(rawTailSource + "\nrawPlaybackTail", context);
    const [button, feedback, raw] = rawPlaybackTail("FFmpeg.Transcode-test.log").children;
    assert.equal(raw.hidden, true);
    assert.equal(requests.length, 0);
    await button.click();
    assert.deepEqual(requests, [["RemoteAdminDesk/LogTail", "FFmpeg.Transcode-test.log"]]);
    assert.equal(raw.textContent, "first\nsecond\nthird");
    assert.equal(raw.hidden, false);
    assert.match(feedback.textContent, /96 KiB/);
    await button.click();
    assert.equal(raw.hidden, true);
    assert.equal(raw.textContent, "");
});

test("event charts distinguish capped dates, partially covered weeks, and observed zeroes", () => {
    const rows = [{ date: "2026-11-03", starts: 4 }, { date: "2026-11-04", starts: 3, signIns: 2 }];
    const daily = desk.trendRange(7, timestamp(6));
    const boundary = Date.parse("2026-11-03T02:00:00Z");
    const buckets = desk.trendBuckets(rows, daily, boundary, ["starts", "signIns"]);
    assert.equal(buckets.length, 7);
    assert.equal(buckets[2].values, null);
    assert.equal(Array.from(buckets.slice(3), bucket => bucket.values.join(":")).join(","), "3:2,0:0,0:0,0:0");
    const ninety = desk.trendRange(90, timestamp(6));
    const byWeek = desk.trendBuckets(rows, ninety, boundary, ["starts"]);
    assert.equal(byWeek.length, 13);
    assert.equal(byWeek.at(-1).covered, 4);
    assert.equal(byWeek.at(-1).length, 6);
    assert.equal(byWeek.at(-1).values[0], 3);
});

test("dense indexed history renders on every range, including older UTC buckets", () => {
    const measured = "2026-11-20T14:31:00Z";
    const days = [
        { date: "2026-11-20", movies: 121, episodes: 501 },
        { date: "2026-11-14", movies: 1 },
        { date: "2026-10-22", movies: 1 },
        { date: "2026-08-23", episodes: 1 }
    ];
    const hours = [{ hour: 23, movies: 121, episodes: 501 }];
    for (const [daysCount, expected] of [[1, 622], [7, 623], [30, 624], [90, 625]]) {
        const range = desk.trendRange(daysCount, measured);
        const buckets = desk.trendBuckets(range.hourly ? hours : days, range, range.start, ["movies", "episodes"]);
        assert.equal(buckets.reduce((sum, bucket) => sum + bucket.covered, 0), range.count);
        assert.equal(buckets.flatMap(bucket => bucket.values).reduce((sum, value) => sum + value, 0), expected);
    }
    const limitedBoundary = Date.parse("2026-11-20T13:00:00Z");
    const week = desk.trendRange(7, measured);
    assert(desk.trendBuckets(days, week, limitedBoundary, ["movies"]).every(bucket => bucket.values === null));
    const emptyWeek = desk.trendBuckets([], week, week.start, ["movies"]);
    assert(emptyWeek.every(bucket => bucket.covered === 1 && bucket.values[0] === 0));
});

test("event and storage selectors update their own charts and default storage to seven days", () => {
    const select = name => html.match(new RegExp('<select data-desk="' + name + '">([^<]*(?:<option[^>]+>[^<]*</option>)+)</select>'))[1];
    const options = name => Array.from(select(name).matchAll(/value="(\d+)"/g), match => Number(match[1]));
    assert.deepEqual(options("visual-window"), [1, 7, 30, 90]);
    assert.deepEqual(options("capacity-window"), [7, 30, 90]);
    assert.match(select("capacity-window"), /value="7" selected/);

    const start = script.indexOf('        field("visual-window").addEventListener');
    const end = script.indexOf('        async function restoreDashboard', start);
    const listeners = new Map();
    const rendered = [];
    vm.runInNewContext(script.slice(start, end), {
        field: name => ({ addEventListener: (event, fn) => {
            assert.equal(event, "change");
            listeners.set(name, fn);
        } }),
        renderEventCharts: () => rendered.push("events"),
        renderCapacityChart: () => rendered.push("storage")
    });
    listeners.get("capacity-window")();
    assert.deepEqual(rendered, ["storage"]);
    listeners.get("visual-window")();
    assert.deepEqual(rendered, ["storage", "events"]);
});

test("24-hour view uses rolling hourly buckets anchored to the source's collection time", () => {
    const measured = "2026-11-07T03:30:00Z";
    const range = desk.trendRange(1, measured);
    const rows = [{ hour: 0, movies: 4 }, { hour: 23, movies: 1, episodes: 2 }];
    const buckets = desk.trendBuckets(rows, range, Date.parse("2026-11-06T04:00:00Z"), ["movies", "episodes"]);
    assert.equal(new Date(buckets[0].start).toISOString(), "2026-11-06T03:30:00.000Z");
    assert.equal(buckets[0].values, null);
    assert.equal(buckets[1].values.join(":"), "0:0");
    assert.equal(buckets[23].values.join(":"), "1:2");
});

test("free-space trends leave missing readings blank and forecast only with total capacity and sufficient history", () => {
    const source = volume(435);
    const days = [[0, 500], [4, 480], [8, 460], [13, 435]].map(([offset, free]) =>
        ({ date: timestamp(offset).slice(0, 10), sampledAt: timestamp(offset), freeBytes: free * gib }));
    const sample = { mount: source.mount, days, totalBytes: source.total };
    const range = desk.trendRange(30, timestamp(13));
    const trend = desk.capacityTrend(sample, range);
    assert.equal(trend.observations, 4);
    assert(trend.buckets.some(bucket => bucket.values === null));
    assert.equal(trend.projection.results.find(result => result.window === 30).days, 67);
    const hourly = desk.capacityTrend({ mount: sample.mount, days: [{ ...days.at(-1), sampledAt: null }] },
        desk.trendRange(1, timestamp(13)));
    assert.equal(hourly.observations, 0);
    assert.equal(hourly.projection, null);
});

test("storage volumes sort by total capacity with a fallback for older charts", () => {
    const saved = [
        { mount: "/", totalBytes: 500 * gib, days: [{ freeBytes: 400 * gib }] },
        { mount: "/unknown", days: [{ freeBytes: 300 * gib }] },
        { mount: "/mnt/media", days: [{ freeBytes: 200 * gib }] }
    ];
    const ordered = desk.largestVolumesFirst(saved, [{ mount: "/mnt/media", total: 15 * 1024 * gib }]);
    assert.equal(Array.from(ordered, volume => volume.mount).join(","), "/mnt/media,/,/unknown");
    assert.equal(saved[0].mount, "/");
});

test("log analysis finds signatures, retains evidence, and respects its line budget", () => {
    const log = [
        "ignored: database disk image is malformed", "[12:00:03] [ERR] FFmpeg exited with code 1",
        "[12:01:00] [WRN] Something unexpected", "Conversion failed.", "Permission denied"
    ].join("\n");
    const outcome = desk.analyze(log, 4);
    assert.equal(outcome.offset, 1);
    assert.equal(outcome.levels.error, 1);
    assert.equal(outcome.levels.warning, 1);
    assert.equal(outcome.findings.find(value => value.title === "FFmpeg or encoder failure").count, 2);
    assert.equal(outcome.findings.find(value => value.title === "Permission or ownership issue").examples[0].line, 5);
    assert(!outcome.findings.some(value => /SQLite/.test(value.title)));
    const blankLines = desk.analyze("[12:00:03] [ERR] FFmpeg exited with code 1\n\n    \n");
    assert.equal(blankLines.lines, 1);
});

test("daily capacity readings forecast runway and reset after cleanup", () => {
    let history = [];
    for (const point of [[0, 500], [2, 480], [4, 460], [4, 444]]) {
        history = desk.recordStorage(history, report(...point));
    }
    assert.equal(history.length, 3);
    const result = desk.forecast(history, volume(460));
    assert.equal(result.results[0].rate, 10 * gib);
    assert.equal(result.results[0].days, 36);
    history = desk.recordStorage(history, report(5, 760));
    const after = desk.forecast(history, volume(760));
    assert(after.reset);
    assert.equal(after.series.length, 1);
    assert.equal(after.results.length, 0);
});

test("an incomplete or unmapped arrival sample cannot produce a capacity ETA", () => {
    const mount = { locations: [{ path: "/mnt/media/tv", mount: "/mnt/media" }] };
    const rows = desk.additions([1, 2, 3].map(n => ({ Name: "Test " + n, DateCreated: timestamp(n),
        MediaSources: [{ Path: "/mnt/media/tv/" + n, Size: 10 * gib, Protocol: "File" }] })));
    const now = Date.parse(timestamp(9));
    assert.equal(desk.arrivalRates(rows, mount, true, now).size, 0);
    assert.equal(desk.arrivalRates(rows, mount, false, now).get(volume(460).mount).rate, gib);
    assert.equal(desk.arrivalRates(rows, { locations: [] }, false, now).size, 0);
    rows[2].sources = rows[1].sources;
    assert.equal(desk.arrivalRates(rows, mount, false, now).get(volume(460).mount).rate, 20 * gib / 30);
});

test("popularity counts starts and unique users without counting stop events twice", () => {
    const report = desk.playbackSummary([
        { Type: "VideoPlayback", UserId: "a", ItemId: "film", Name: "Alice is playing A film on TV", Date: timestamp(1) },
        { Type: "VideoPlaybackStopped", UserId: "a", ItemId: "film", Date: timestamp(1) },
        { Type: "VideoPlayback", UserId: "b", ItemId: "film", Name: "Bob is playing A film on Web", Date: timestamp(2) },
        { Type: "AuthenticationSucceeded", UserId: "a" }
    ], [{ Id: "a", Name: "Alice" }]);
    assert.equal(report.starts, 2);
    assert.equal(report.stops, 1);
    assert.equal(report.titles[0].name, "A film");
    assert.equal(report.titles[0].viewers.size, 2);
    assert.equal(report.users[0].name, "Alice");
});

test("recent-media charts use date buckets and count distinct codecs per item", () => {
    const now = new Date(2026, 10, 18, 12).getTime();
    const today = new Date(now - 3600000).toISOString();
    const rows = [
        { date: today, sources: [
            { MediaStreams: [{ Type: "Video", Codec: "H264" }, { Type: "Video", Codec: "h264" }, { Type: "Audio", Codec: "aac" }] },
            { MediaStreams: [{ Type: "Video", Codec: "HEVC" }] }
        ] },
        { date: today, sources: [{ MediaStreams: [{ Type: "Video", Codec: "h264" }] }] },
        { date: new Date(now + 3600000).toISOString(), sources: [] }
    ];
    assert.equal(desk.codecCounts(rows).get("h264"), 2);
    assert.equal(desk.codecCounts(rows).get("hevc"), 1);
    assert.equal(desk.codecCounts(rows).has("aac"), false);
    const bars = desk.recentDayCounts(rows, now);
    assert.equal(bars.length, 7);
    assert.equal(bars.at(-1).value, 2);
    assert.equal(bars.slice(0, -1).reduce((sum, bar) => sum + bar.value, 0), 0);
});

test("all dashboard tab buttons point to distinct labelled panels", () => {
    const tabs = [...html.matchAll(/id="(rad-tab-[a-z]+)" role="tab" aria-controls="(rad-panel-[a-z]+)"/g)];
    assert.equal(tabs.length, 6);
    assert.equal(new Set(tabs.map(tab => tab[2])).size, tabs.length);
    for (const [, button, panel] of tabs) {
        assert(html.includes(`id="${panel}" role="tabpanel" aria-labelledby="${button}"`));
    }
});


test("compatibility accepts omitted zero parts and rejects untested Jellyfin releases", () => {
    assert.equal(desk.matchesVersion("12.4", "12.4.0"), true);
    assert.equal(desk.matchesVersion("12.4.0.0", "12.4.0"), true);
    assert.equal(desk.matchesVersion("12.4.1", "12.4.0"), false);
    assert.equal(desk.matchesVersion("12.5.0", "12.4.0"), false);
    assert.equal(desk.matchesVersion("12.4.0-rc1", "12.4.0"), false);
    assert.equal(desk.matchesVersion("12.4.0", null), false);
});

test("historical activity distinguishes successful logins, connections, and streaming", () => {
    const now = Date.parse(timestamp(10));
    const data = desk.playbackSummary([
        { Type: "AuthenticationSucceeded", UserId: "user", Date: timestamp(9) },
        { Type: "SessionStarted", UserId: "user", Date: timestamp(9) },
        { Type: "VideoPlayback", UserId: "user", Date: timestamp(9) },
        { Type: "VideoPlaybackStopped", UserId: "user", Date: timestamp(9) },
        { Type: "AuthenticationFailed", UserId: "user", Date: timestamp(9) }
    ], [{ Id: "user", Name: "Lab user" }]);
    assert.equal(data.logins, 1);
    assert.equal(data.sessions, 1);
    assert.equal(data.starts, 1);
    assert.equal(data.stops, 1);
    assert.equal(data.users[0].logins, 1);
    assert.equal(data.users[0].sessions, 1);
    const buckets = desk.activityBuckets(data.events, 7, now);
    assert.equal(buckets.reduce((n, bucket) => n + bucket.logins + bucket.sessions + bucket.starts, 0), 3);
});
