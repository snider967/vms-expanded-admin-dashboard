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
