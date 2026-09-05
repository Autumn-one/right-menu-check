import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const source = readFileSync(new URL("../internal/api/assets/dashboard.js", import.meta.url), "utf8");
const html = readFileSync(new URL("../internal/api/assets/dashboard.html", import.meta.url), "utf8");

class Element {
  constructor() {
    this.children = [];
    this.listeners = new Map();
    this.textContent = "";
  }
  get firstChild() { return this.children[0]; }
  appendChild(child) { child.parent = this; this.children.push(child); }
  remove() { this.parent.children.splice(this.parent.children.indexOf(this), 1); }
  addEventListener(name, callback) { this.listeners.set(name, callback); }
  async click() { await this.listeners.get("click")?.(); }
}

async function flush() {
  await new Promise(resolve => setImmediate(resolve));
}

async function dashboard() {
  const elements = new Map([...html.matchAll(/id="([^"]+)"/g)].map(match => [match[1], new Element()]));
  const requests = [];
  let timer;
  let device = {
    machineId: "a".repeat(64), startupCount: 1, totalDurationMilliseconds: 0,
    activeSessionCount: 1, abnormalSessionCount: 0,
    lastStartedAtUtc: "2026-09-06T00:00:00Z", lastSeenAtUtc: "2026-09-06T00:00:00Z",
  };
  vm.runInNewContext(source, {
    document: {
      getElementById: id => {
        assert.ok(elements.has(id), `Unknown element: ${id}`);
        return elements.get(id);
      },
      createElement: () => new Element(),
    },
    sessionStorage: { getItem: () => "test-only-token" },
    window: { setInterval: (callback, delay) => { timer = callback; assert.equal(delay, 30000); } },
    URLSearchParams, Intl, Date,
    fetch: async path => {
      requests.push(path);
      const url = new URL(path, "http://localhost");
      let payload;
      if (url.pathname === "/v1/admin/summary") {
        payload = {
          machineCount: 1, activeMachineCount: device.activeSessionCount,
          activeSessionCount: device.activeSessionCount, startupCount: device.startupCount,
          sessionCount: device.startupCount, totalDurationMilliseconds: device.totalDurationMilliseconds,
          activeDurationMilliseconds: device.activeDurationMilliseconds,
        };
      } else if (url.pathname === "/v1/admin/machines") {
        payload = { items: [{ ...device }] };
      } else {
        assert.equal(url.pathname, "/v1/admin/sessions");
        payload = { items: Array.from({ length: 100 }, () => ({
          startedAtUtc: device.lastStartedAtUtc, endedAtUtc: null,
          durationMilliseconds: 0, exitKind: "active",
        })) };
      }
      return { status: 200, ok: true, json: async () => payload };
    },
  });
  await flush();
  return {
    elements, requests,
    change: changes => { device = { ...device, ...changes }; },
    select: async () => { await elements.get("deviceRows").children[0].children[1].children[0].click(); },
    refresh: async () => { await elements.get("refreshButton").click(); await flush(); },
    tick: async () => { await timer(); await flush(); },
  };
}

for (const method of ["refresh", "tick"]) {
  test(`${method} updates selected device fields after heartbeat, end, and restart`, async () => {
    const page = await dashboard();
    await page.select();
    const lastSeen = page.elements.get("detailLastSeen").textContent;
    page.change({ lastSeenAtUtc: "2026-09-06T00:02:00Z" });
    await page[method]();
    assert.notEqual(page.elements.get("detailLastSeen").textContent, lastSeen);
    page.change({ activeSessionCount: 0, totalDurationMilliseconds: 300000 });
    await page[method]();
    assert.equal(page.elements.get("detailStatus").className, "status-badge offline");
    assert.equal(page.elements.get("detailDuration").textContent, page.elements.get("totalDuration").textContent);
    page.change({ activeSessionCount: 1, startupCount: 2 });
    await page[method]();
    assert.equal(page.elements.get("detailStatus").className, "status-badge online");
    assert.equal(page.elements.get("detailStarts").textContent, "2");
  });
}

test("refresh preserves history pagination; reselect resets it", async () => {
  const page = await dashboard();
  await page.select();
  await page.elements.get("nextSessions").click();
  await flush();
  await page.refresh();
  assert.equal(new URL(page.requests.at(-1), "http://localhost").searchParams.get("offset"), "100");
  await page.select();
  assert.equal(new URL(page.requests.at(-1), "http://localhost").searchParams.get("offset"), "0");
});

test("refresh does not reselect a closed detail or request its history", async () => {
  const page = await dashboard();
  await page.select();
  await page.elements.get("closeDetail").click();
  const requestCount = page.requests.length;
  await page.refresh();
  assert.equal(page.elements.get("detailMachine").textContent, "-");
  assert.equal(page.elements.get("detailStatus").className, "status-badge idle");
  assert.equal(page.elements.get("sessionRows").children.length, 0);
  assert.ok(page.requests.slice(requestCount).every(path => !path.startsWith("/v1/admin/sessions")));
});

test("cumulative duration adds confirmed live time once and supports older APIs", async () => {
  const page = await dashboard();
  await page.select();
  const assertDuration = expected => {
    for (const id of ["totalDuration", "detailDuration"]) {
      assert.equal(page.elements.get(id).textContent, expected);
    }
    assert.equal(page.elements.get("deviceRows").children[0].children[3].textContent, expected);
  };
  assertDuration("0\u79d2");
  page.change({ totalDurationMilliseconds: 60000, activeDurationMilliseconds: 120000 });
  await page.refresh();
  assertDuration("3\u5206\u949f");
  await page.tick();
  assertDuration("3\u5206\u949f");
  page.change({ totalDurationMilliseconds: 180000, activeDurationMilliseconds: 0, activeSessionCount: 0 });
  await page.refresh();
  assertDuration("3\u5206\u949f");
  page.change({ activeDurationMilliseconds: undefined });
  await page.refresh();
  assertDuration("3\u5206\u949f");
});
