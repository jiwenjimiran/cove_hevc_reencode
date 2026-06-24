import React from "@cove/runtime/react";

const e = React.createElement;
const EXT = "cove.community.ai.hevc-reencode";
const API = "/api/ext/hevc-reencode";

function buildFields(settings) {
  const outputFormat = settings.outputFormat === "av1" ? "av1" : "hevc";
  const encoderOptions = outputFormat === "av1" ? ["auto", "av1_nvenc", "av1_amf"] : ["auto", "hevc_nvenc", "hevc_amf"];
  return [
    { key: "encoderPreference", label: "GPU Encoder", type: "select", def: "auto", options: encoderOptions },
    { key: "outputFormat", label: "Output Format", type: "select", def: "hevc", options: ["hevc", "av1"] },
    { key: "maxConcurrentEncodes", label: "Encoding Engines", type: "number", def: -1, min: -1, warning: "WARNING! selecting more than the number of encoding engines will cause GPU thrashing." },
    outputFormat === "av1"
      ? { key: "av1Cq", label: "AV1 Quality Level (CQ)", type: "number", def: 30, min: 0, max: 63 }
      : { key: "cq", label: "HEVC Quality Level (CQ)", type: "number", def: 28, min: 0, max: 51 },
    outputFormat === "av1"
      ? { key: "av1LowBitrateCq", label: "AV1 Low-Bitrate CQ", type: "number", def: 36, min: 0, max: 63 }
      : { key: "cqLowBitrate", label: "HEVC Low-Bitrate CQ", type: "number", def: 34, min: 0, max: 51 },
    { key: "preset", label: "NVENC Preset", type: "select", def: "p7", options: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"] },
    { key: "skipCodecs", label: "Skip Codecs", type: "chips", def: ["hevc", "av1", "vp9"], chips: [{ value: "hevc", label: "H.265" }, { value: "av1", label: "AV1" }, { value: "vp9", label: "VP9" }, { value: "vp8", label: "VP8" }] },
    { key: "skipFailedTag", label: "Skip Previously Failed", type: "bool", def: true },
    { key: "remuxIncompatibleContainer", label: "Remux Incompatible Containers", type: "bool", def: true },
    { key: "outputSuffix", label: "Output Filename Suffix", type: "text", def: "", disabledWhen: (s) => s.deleteAfterConvert },
    { key: "copyMetadataOnSuffix", label: "Copy Metadata on Suffix", type: "bool", def: true, disabledWhen: (s) => s.deleteAfterConvert },
    { key: "minSavingsPct", label: "Minimum Savings %", type: "number", def: 15, min: 0, max: 100 },
    { key: "gpuIndex", label: "GPU Index", type: "number", def: 0, min: 0 },
    { key: "enableRetries", label: "Enable Aggressive Retries", type: "bool", def: true },
    outputFormat === "av1"
      ? { key: "av1AggressiveCq", label: "AV1 Aggressive Retry CQ", type: "number", def: 38, min: 0, max: 63 }
      : { key: "aggressiveCq", label: "HEVC Aggressive Retry CQ", type: "number", def: 34, min: 0, max: 51 },
    outputFormat === "av1"
      ? { key: "av1UltraAggressiveCq", label: "AV1 Ultra-Aggressive CQ Ceiling", type: "number", def: 44, min: 0, max: 63 }
      : { key: "ultraAggressiveCq", label: "HEVC Ultra-Aggressive CQ Ceiling", type: "number", def: 40, min: 0, max: 51 },
    { key: "stripMetadata", label: "Wipe Container Metadata", type: "bool", def: false },
    { key: "embedStashMetadata", label: "Embed Cove Metadata", type: "bool", def: false, parent: "stripMetadata" }
  ];
}

const DEFAULTS = {
  tagOnFailure: true,
  reencodeFailedTag: "reencode_failed",
  deleteAfterConvert: true,
  outputFormat: "hevc",
  encoderPreference: "auto",
  maxConcurrentEncodes: -1,
  cq: 28,
  cqLowBitrate: 34,
  av1Cq: 30,
  av1LowBitrateCq: 36,
  preset: "p7",
  skipCodecs: ["hevc", "av1", "vp9"],
  skipFailedTag: true,
  remuxIncompatibleContainer: true,
  outputSuffix: "",
  copyMetadataOnSuffix: true,
  minSavingsPct: 15,
  gpuIndex: 0,
  enableRetries: true,
  aggressiveCq: 34,
  ultraAggressiveCq: 40,
  av1AggressiveCq: 38,
  av1UltraAggressiveCq: 44,
  stripMetadata: false,
  embedStashMetadata: false
};

async function request(path, options = {}) {
  const res = await fetch(path, { ...options, headers: { "Content-Type": "application/json", ...(options.headers || {}) } });
  const text = await res.text();
  const body = text ? JSON.parse(text) : null;
  if (!res.ok) throw new Error((body && (body.message || body.error || body.detail)) || text || res.statusText);
  return body;
}

function normalize(value) {
  return { ...DEFAULTS, ...(value || {}) };
}

function HevcReencodeSettingsPanel() {
  const { useEffect, useMemo, useState } = React;
  const [settings, setSettings] = useState(normalize(null));
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState("");
  const [health, setHealth] = useState(null);
  const [advancedOpen, setAdvancedOpen] = useState(true);
  const fields = useMemo(() => buildFields(settings), [settings.outputFormat, settings.deleteAfterConvert, settings.stripMetadata]);

  useEffect(() => {
    let cancelled = false;
    request(`${API}/settings`)
      .then((data) => { if (!cancelled) setSettings(normalize(data)); })
      .catch((err) => { if (!cancelled) setMessage(err.message || "Failed to load settings."); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    if (loading) return;
    setSaving(true);
    const handle = setTimeout(() => {
      request(`${API}/settings`, { method: "PUT", body: JSON.stringify(settings) })
        .then((data) => { setSettings(normalize(data)); setMessage("Settings saved."); })
        .catch((err) => setMessage(err.message || "Failed to save settings."))
        .finally(() => setSaving(false));
    }, 350);
    return () => clearTimeout(handle);
  }, [settings]);

  const changed = useMemo(() => {
    const map = {};
    for (const [key, value] of Object.entries(DEFAULTS)) {
      map[key] = JSON.stringify(settings[key]) !== JSON.stringify(value);
    }
    return map;
  }, [settings]);

  function update(key, value) {
    setSettings((current) => key === "outputFormat"
      ? { ...current, outputFormat: value, encoderPreference: "auto" }
      : { ...current, [key]: value });
  }

  function reset(key) {
    update(key, DEFAULTS[key]);
  }

  async function checkHealth() {
    setMessage("");
    setHealth(null);
    try {
      setHealth(await request(`${API}/encoder-health`));
    } catch (err) {
      setHealth({ ok: false, error: err.message || "Encoder check failed." });
    }
  }

  if (loading) return e("p", { className: "hevc-muted" }, "Loading HEVC reencode settings...");

  return e("div", { className: "hevc-panel" }, [
    e("div", { className: "hevc-basic", key: "basic" }, [
      e(BoolField, { key: "tagOnFailure", label: "Add Tag Indicating Conversion Failed", value: settings.tagOnFailure, onChange: (v) => update("tagOnFailure", v) }),
      e(Field, { key: "failedTag", label: "Failure Tag Name" }, e("input", { className: "hevc-input", value: settings.reencodeFailedTag, onChange: (ev) => update("reencodeFailedTag", ev.target.value) })),
      e(BoolField, { key: "deleteAfter", label: "Delete Original After Successful Re-encode", value: settings.deleteAfterConvert, onChange: (v) => update("deleteAfterConvert", v) })
    ]),
    e("div", { className: "hevc-advanced", key: "advanced" }, [
      e("button", { className: "hevc-advanced-toggle", type: "button", onClick: () => setAdvancedOpen(!advancedOpen) }, `${advancedOpen ? "▾" : "▸"} Advanced Settings`),
      advancedOpen ? e("div", { className: "hevc-fields" }, fields.map((field) => {
        const disabled = field.disabled || (field.disabledWhen && field.disabledWhen(settings)) || (field.parent && !settings[field.parent]);
        return e(Field, { key: field.key, label: field.label, hint: `(default: ${formatDefault(field.def, field)})`, disabled }, [
          e(Editor, { field, value: settings[field.key], disabled, onChange: (value) => update(field.key, value) }),
          e("button", { type: "button", className: "hevc-reset", disabled: !changed[field.key], onClick: () => reset(field.key) }, "Reset"),
          field.warning ? e("span", { className: "hevc-warning" }, field.warning) : null
        ]);
      })) : null
    ]),
    e("div", { className: "hevc-actions", key: "actions" }, [
      e("button", { type: "button", className: "hevc-button", onClick: checkHealth }, "Check encoder"),
      e("button", { type: "button", className: "hevc-button", onClick: () => request(`/api/extensions/${EXT}/jobs/reencode-all/run`, { method: "POST" }).then(() => setMessage("Re-encode all job queued.")).catch((err) => setMessage(err.message)) }, "Re-encode all videos"),
      e("span", { className: "hevc-muted" }, saving ? "Saving..." : message)
    ]),
    health ? e("pre", { className: `hevc-health ${health.ok ? "ok" : "bad"}`, key: "health" }, JSON.stringify(health, null, 2)) : null,
    e("p", { className: "hevc-note", key: "note" }, "Reencoding runs inside Cove using local ffmpeg/ffprobe with GPU HEVC and AV1 encoders.")
  ]);
}

function Field({ label, hint, disabled, children }) {
  return e("label", { className: `hevc-field ${disabled ? "disabled" : ""}` }, [
    e("span", { className: "hevc-label" }, [label, hint ? e("span", { className: "hevc-hint" }, ` ${hint}`) : null]),
    e("span", { className: "hevc-control" }, children)
  ]);
}

function BoolField({ label, value, onChange }) {
  return e("label", { className: "hevc-check" }, [
    e("input", { type: "checkbox", checked: !!value, onChange: (ev) => onChange(ev.target.checked) }),
    e("span", null, label)
  ]);
}

function Editor({ field, value, disabled, onChange }) {
  if (field.type === "bool") return e("input", { type: "checkbox", checked: !!value, disabled, onChange: (ev) => onChange(ev.target.checked) });
  if (field.type === "number") return e("input", { className: "hevc-input short", type: "number", min: field.min, max: field.max, value, disabled, onChange: (ev) => onChange(Number(ev.target.value)) });
  if (field.type === "select") return e("select", { className: "hevc-input", value, disabled, onChange: (ev) => onChange(ev.target.value) }, field.options.map((option) => e("option", { key: option, value: option }, formatOption(option))));
  if (field.type === "chips") {
    const selected = new Set(Array.isArray(value) ? value : []);
    return e("span", { className: "hevc-chips" }, field.chips.map((chip) => e("button", {
      key: chip.value,
      type: "button",
      disabled,
      className: selected.has(chip.value) ? "selected" : "",
      onClick: () => {
        const next = new Set(selected);
        next.has(chip.value) ? next.delete(chip.value) : next.add(chip.value);
        onChange(Array.from(next));
      }
    }, chip.label)));
  }
  return e("input", { className: "hevc-input", value: value || "", disabled, onChange: (ev) => onChange(ev.target.value) });
}

function formatDefault(value, field) {
  if (Array.isArray(value) && field.chips) return value.map((item) => field.chips.find((chip) => chip.value === item)?.label || item).join(", ");
  if (field.key === "maxConcurrentEncodes" && value === -1) return "auto";
  return String(value);
}

function formatOption(option) {
  if (option === "auto") return "Auto";
  if (option === "hevc") return "HEVC";
  if (option === "av1") return "AV1";
  if (option === "hevc_nvenc") return "NVIDIA NVENC HEVC";
  if (option === "hevc_amf") return "AMD AMF HEVC";
  if (option === "av1_nvenc") return "NVIDIA NVENC AV1";
  if (option === "av1_amf") return "AMD AMF AV1";
  if (option === "p1") return "p1 (fastest)";
  if (option === "p4") return "p4 (balanced)";
  if (option === "p7") return "p7 (best compression)";
  return option;
}

async function queueHevcReencode(_action, payload) {
  const ids = (payload?.entityIds || payload?.selectedIds || []).map(Number).filter((id) => Number.isInteger(id) && id > 0);
  if (ids.length === 0) return { cancelled: true, suppressToast: true };
  const res = await request(`${API}/queue`, {
    method: "POST",
    body: JSON.stringify({ entityIds: ids, selectedIds: ids })
  });
  return { ...res, description: `Reencode queued for ${ids.length} video${ids.length === 1 ? "" : "s"}.` };
}

export default {
  components: { HevcReencodeSettingsPanel },
  actionHandlers: { queueHevcReencode },
  handlers: { queueHevcReencode }
};
