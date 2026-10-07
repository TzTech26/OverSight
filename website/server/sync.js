'use strict';
/**
 * NAS release sync.
 *
 * Source of truth = the Oversight `releases/` folder on your NAS (the one with
 * manifest.json + verified/ + beta/). This module mirrors manifest.json and the
 * per-channel installer .exe files into data/releases/ (a local cache) so the
 * store keeps serving even if the NAS is offline, and downloads are fast.
 *
 * Runs once on boot and then every SYNC_INTERVAL_HOURS.
 */
const fs = require('fs');
const path = require('path');
const config = require('./config');

const CHANNELS = ['verified', 'beta'];
const CACHE = config.RELEASES_CACHE;

// PowerShell writes JSON as UTF-8 *with a BOM*; strip it before parsing.
function readJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^﻿/, ''));
}

let status = {
  lastRunUtc: null,
  ok: false,
  message: 'No sync yet.',
  copied: 0,
  skipped: 0,
  source: config.nasReleasesDir || '(not set)'
};
const STATUS_PATH = path.join(config.DATA_DIR, 'sync-status.json');
try { status = { ...status, ...JSON.parse(fs.readFileSync(STATUS_PATH, 'utf8')) }; } catch { /* first run */ }

function saveStatus() {
  try { fs.writeFileSync(STATUS_PATH, JSON.stringify(status, null, 2)); } catch { /* ignore */ }
}

function copyIfChanged(src, dst) {
  try {
    const s = fs.statSync(src);
    let need = true;
    if (fs.existsSync(dst)) {
      const d = fs.statSync(dst);
      need = d.size !== s.size || d.mtimeMs < s.mtimeMs;
    }
    if (need) {
      fs.mkdirSync(path.dirname(dst), { recursive: true });
      fs.copyFileSync(src, dst);
      return 'copied';
    }
    return 'skipped';
  } catch (err) {
    console.error(`[sync] failed to copy ${src}: ${err.message}`);
    return 'error';
  }
}

/** Perform one sync pass. Returns the status object. */
function runSync() {
  const src = config.nasReleasesDir;
  status.lastRunUtc = new Date().toISOString();
  status.source = src || '(not set)';
  status.copied = 0; status.skipped = 0;

  if (!src) { status.ok = false; status.message = 'NAS_RELEASES_DIR is not set.'; saveStatus(); return status; }
  const manifestSrc = path.join(src, 'manifest.json');
  if (!fs.existsSync(manifestSrc)) {
    status.ok = false;
    status.message = `Cannot see manifest.json at ${manifestSrc}. Is the NAS mounted?`;
    saveStatus(); return status;
  }

  let manifest;
  try { manifest = readJson(manifestSrc); }
  catch (err) { status.ok = false; status.message = `manifest.json is invalid: ${err.message}`; saveStatus(); return status; }

  for (const rel of manifest.releases || []) {
    const channel = CHANNELS.includes(rel.channel) ? rel.channel : null;
    const file = rel.installer && rel.installer.file;
    if (!channel || !file) continue;
    const from = path.join(src, channel, file);
    if (!fs.existsSync(from)) { console.warn(`[sync] missing installer on NAS: ${from}`); continue; }
    const result = copyIfChanged(from, path.join(CACHE, channel, file));
    if (result === 'copied') status.copied++;
    else if (result === 'skipped') status.skipped++;
  }

  // Write the manifest last so a half-copied sync never advertises missing files.
  fs.writeFileSync(path.join(CACHE, 'manifest.json'), JSON.stringify(manifest, null, 2));
  status.ok = true;
  status.message = `Synced ${manifest.releases ? manifest.releases.length : 0} releases (${status.copied} new, ${status.skipped} up-to-date).`;
  saveStatus();
  console.log(`[sync] ${status.message}`);
  return status;
}

/** Read the cached manifest (what the site serves from). */
function getManifest() {
  try { return readJson(path.join(CACHE, 'manifest.json')); }
  catch { return null; }
}

/**
 * Releases the buyer may pick, filtered by which channels are enabled in
 * settings, newest first, with display labels. Only includes files present in
 * the local cache.
 */
function availableReleases() {
  const m = getManifest();
  if (!m) return [];
  const enabled = config.settings.channels;
  return (m.releases || [])
    .filter((r) => enabled[r.channel])
    .filter((r) => r.installer && fs.existsSync(path.join(CACHE, r.channel, r.installer.file)))
    .map((r) => ({
      version: r.version,
      channel: r.channel,
      file: r.installer.file,
      sizeMB: r.installer.size ? (r.installer.size / 1048576).toFixed(1) : null,
      sha256: r.installer.sha256 || null,
      builtUtc: r.builtUtc || null,
      isBeta: r.channel === 'beta',
      label: `${r.version}${r.channel === 'beta' ? ' — Work in progress (beta)' : ' — Verified'}`
    }))
    .sort((a, b) => cmpVersion(b.version, a.version));
}

/** Default selection: latest of the preferred channel, else newest available. */
function defaultRelease() {
  const list = availableReleases();
  if (!list.length) return null;
  const pref = config.settings.defaultChannel;
  return list.find((r) => r.channel === pref) || list[0];
}

/** Resolve a version+channel to a validated absolute file path, or null. */
function resolveFile(version, channel) {
  if (!CHANNELS.includes(channel)) return null;
  const rel = availableReleases().find((r) => r.version === version && r.channel === channel);
  if (!rel) return null;
  // Guard against path traversal — filename must be a plain basename.
  if (rel.file.includes('/') || rel.file.includes('\\') || rel.file.includes('..')) return null;
  const abs = path.join(CACHE, channel, rel.file);
  return fs.existsSync(abs) ? { abs, name: rel.file, sha256: rel.sha256 } : null;
}

function getStatus() { return status; }

let timer = null;
function startScheduler() {
  runSync(); // on boot
  const hours = Math.max(1, config.syncIntervalHours || 24);
  if (timer) clearInterval(timer);
  timer = setInterval(runSync, hours * 3600 * 1000);
  console.log(`[sync] scheduled every ${hours}h`);
}

function cmpVersion(a, b) {
  const pa = String(a).split('.').map((n) => parseInt(n, 10) || 0);
  const pb = String(b).split('.').map((n) => parseInt(n, 10) || 0);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    if ((pa[i] || 0) !== (pb[i] || 0)) return (pa[i] || 0) - (pb[i] || 0);
  }
  return 0;
}

module.exports = {
  runSync, getManifest, availableReleases, defaultRelease, resolveFile, getStatus, startScheduler
};
