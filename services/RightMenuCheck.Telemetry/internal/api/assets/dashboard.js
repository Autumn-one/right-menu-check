(() => {
  "use strict";

  const pageSize = 100;
  const state = {
    token: sessionStorage.getItem("rmc-admin-token") || "",
    devices: [],
    deviceOffset: 0,
    selected: null,
    sessionOffset: 0,
    loading: false,
  };

  const byId = (id) => document.getElementById(id);
  const loginDialog = byId("loginDialog");
  const statusBar = byId("statusBar");

  function setStatus(message) {
    statusBar.textContent = message;
  }

  async function api(path) {
    const response = await fetch(path, {
      headers: { Authorization: `Bearer ${state.token}` },
      cache: "no-store",
    });
    if (response.status === 401) {
      state.token = "";
      sessionStorage.removeItem("rmc-admin-token");
      showLogin(true);
      throw new Error("unauthorized");
    }
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }
    return response.json();
  }

  function showLogin(invalid) {
    byId("loginError").hidden = !invalid;
    if (!loginDialog.open) loginDialog.showModal();
    byId("tokenInput").focus();
  }

  function formatDuration(value) {
    const totalSeconds = Math.max(0, Math.floor(Number(value || 0) / 1000));
    const days = Math.floor(totalSeconds / 86400);
    const hours = Math.floor((totalSeconds % 86400) / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    if (days > 0) return `${days}天 ${hours}小时`;
    if (hours > 0) return `${hours}小时 ${minutes}分钟`;
    if (minutes > 0) return `${minutes}分钟`;
    return `${totalSeconds}秒`;
  }

  function formatTime(value) {
    if (!value) return "-";
    return new Intl.DateTimeFormat("zh-CN", {
      year: "numeric", month: "2-digit", day: "2-digit",
      hour: "2-digit", minute: "2-digit", second: "2-digit",
      hour12: false,
    }).format(new Date(value));
  }

  function shortMachine(value) {
    return value.length > 26 ? `${value.slice(0, 14)}...${value.slice(-8)}` : value;
  }

  function clear(element) {
    while (element.firstChild) element.firstChild.remove();
  }

  function appendCell(row, value, className = "") {
    const cell = document.createElement("td");
    cell.textContent = value;
    if (className) cell.className = className;
    row.appendChild(cell);
    return cell;
  }

  function statusBadge(label, kind) {
    const badge = document.createElement("span");
    badge.className = `status-badge ${kind}`;
    badge.textContent = label;
    return badge;
  }

  function renderSummary(summary) {
    byId("machineCount").textContent = summary.machineCount.toLocaleString("zh-CN");
    byId("activeMachineCount").textContent = summary.activeMachineCount.toLocaleString("zh-CN");
    byId("activeSessionCount").textContent = summary.activeSessionCount.toLocaleString("zh-CN");
    byId("startupCount").textContent = summary.startupCount.toLocaleString("zh-CN");
    byId("sessionCount").textContent = summary.sessionCount.toLocaleString("zh-CN");
    byId("totalDuration").textContent = formatDuration(summary.totalDurationMilliseconds);
  }

  function renderDevices(page) {
    const body = byId("deviceRows");
    clear(body);
    state.devices = page.items;
    for (const device of page.items) {
      const row = document.createElement("tr");
      const statusCell = document.createElement("td");
      statusCell.appendChild(statusBadge(
        device.activeSessionCount > 0 ? "运行中" : "离线",
        device.activeSessionCount > 0 ? "online" : "offline"));
      row.appendChild(statusCell);

      const machineCell = document.createElement("td");
      const machineButton = document.createElement("button");
      machineButton.type = "button";
      machineButton.className = "device-link";
      machineButton.textContent = shortMachine(device.machineId);
      machineButton.title = device.machineId;
      machineButton.addEventListener("click", () => selectDevice(device));
      machineCell.appendChild(machineButton);
      row.appendChild(machineCell);

      appendCell(row, device.startupCount.toLocaleString("zh-CN"), "numeric");
      appendCell(row, formatDuration(device.totalDurationMilliseconds), "numeric");
      appendCell(row, formatTime(device.lastStartedAtUtc));
      appendCell(row, device.abnormalSessionCount.toLocaleString("zh-CN"), "numeric");
      body.appendChild(row);
    }
    byId("emptyDevices").hidden = page.items.length !== 0;
    const start = page.items.length === 0 ? 0 : state.deviceOffset + 1;
    byId("deviceRange").textContent = `${start}-${state.deviceOffset + page.items.length}`;
    byId("previousDevices").disabled = state.deviceOffset === 0;
    byId("nextDevices").disabled = page.items.length < pageSize;
  }

  async function selectDevice(device) {
    state.selected = device;
    state.sessionOffset = 0;
    byId("detailStatus").className = `status-badge ${device.activeSessionCount > 0 ? "online" : "offline"}`;
    byId("detailStatus").textContent = device.activeSessionCount > 0 ? "运行中" : "离线";
    byId("detailMachine").textContent = device.machineId;
    byId("detailStarts").textContent = device.startupCount.toLocaleString("zh-CN");
    byId("detailDuration").textContent = formatDuration(device.totalDurationMilliseconds);
    byId("detailLastSeen").textContent = formatTime(device.lastSeenAtUtc);
    await loadSessions();
  }

  function renderSessions(page) {
    const body = byId("sessionRows");
    clear(body);
    for (const session of page.items) {
      const row = document.createElement("tr");
      appendCell(row, formatTime(session.startedAtUtc));
      appendCell(row, session.endedAtUtc ? formatTime(session.endedAtUtc) : "运行中");
      appendCell(row, formatDuration(session.durationMilliseconds), "numeric");
      const statusCell = document.createElement("td");
      const label = session.exitKind === "normal" ? "正常" :
        session.exitKind === "abnormal" ? "异常" : "运行中";
      statusCell.appendChild(statusBadge(label, session.exitKind));
      row.appendChild(statusCell);
      body.appendChild(row);
    }
    byId("emptySessions").hidden = page.items.length !== 0;
    byId("previousSessions").disabled = state.sessionOffset === 0;
    byId("nextSessions").disabled = page.items.length < pageSize;
  }

  async function loadSessions() {
    if (!state.selected) return;
    try {
      const query = new URLSearchParams({
        machineId: state.selected.machineId,
        limit: String(pageSize),
        offset: String(state.sessionOffset),
      });
      renderSessions(await api(`/v1/admin/sessions?${query}`));
    } catch (error) {
      if (error.message !== "unauthorized") setStatus(`历史读取失败: ${error.message}`);
    }
  }

  async function loadDashboard() {
    if (!state.token || state.loading) return;
    state.loading = true;
    byId("refreshButton").disabled = true;
    setStatus("正在刷新...");
    try {
      const query = new URLSearchParams({
        limit: String(pageSize),
        offset: String(state.deviceOffset),
      });
      const [summary, machines] = await Promise.all([
        api("/v1/admin/summary"),
        api(`/v1/admin/machines?${query}`),
      ]);
      renderSummary(summary);
      renderDevices(machines);
      if (state.selected) {
        const current = machines.items.find(item => item.machineId === state.selected.machineId);
        if (current) state.selected = current;
        await loadSessions();
      }
      const now = new Date();
      byId("lastUpdated").textContent = `更新于 ${now.toLocaleTimeString("zh-CN", { hour12: false })}`;
      setStatus("数据已刷新");
    } catch (error) {
      if (error.message !== "unauthorized") setStatus(`刷新失败: ${error.message}`);
    } finally {
      state.loading = false;
      byId("refreshButton").disabled = false;
    }
  }

  byId("loginForm").addEventListener("submit", async (event) => {
    event.preventDefault();
    state.token = byId("tokenInput").value.trim();
    try {
      await api("/v1/admin/summary");
      sessionStorage.setItem("rmc-admin-token", state.token);
      loginDialog.close();
      byId("loginError").hidden = true;
      await loadDashboard();
    } catch (error) {
      if (error.message !== "unauthorized") byId("loginError").hidden = false;
    }
  });

  byId("refreshButton").addEventListener("click", loadDashboard);
  byId("logoutButton").addEventListener("click", () => {
    state.token = "";
    sessionStorage.removeItem("rmc-admin-token");
    showLogin(false);
  });
  byId("previousDevices").addEventListener("click", () => {
    state.deviceOffset = Math.max(0, state.deviceOffset - pageSize);
    loadDashboard();
  });
  byId("nextDevices").addEventListener("click", () => {
    state.deviceOffset += pageSize;
    loadDashboard();
  });
  byId("previousSessions").addEventListener("click", () => {
    state.sessionOffset = Math.max(0, state.sessionOffset - pageSize);
    loadSessions();
  });
  byId("nextSessions").addEventListener("click", () => {
    state.sessionOffset += pageSize;
    loadSessions();
  });
  byId("closeDetail").addEventListener("click", () => {
    state.selected = null;
    byId("detailStatus").className = "status-badge idle";
    byId("detailStatus").textContent = "未选择";
    byId("detailMachine").textContent = "-";
    clear(byId("sessionRows"));
  });

  if (state.token) loadDashboard(); else showLogin(false);
  window.setInterval(loadDashboard, 30000);
})();
