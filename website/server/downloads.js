'use strict';
/**
 * Licensed download area (entitlement-gated).
 *  - GET /download          -> version picker (default = latest of preferred channel)
 *  - GET /download/file     -> streams the chosen installer with SHA-256 header
 *  - GET /account           -> user's purchases / license info
 */
const express = require('express');
const fs = require('fs');
const config = require('./config');
const db = require('./db');
const sync = require('./sync');
const { requireAuth, requireEntitlement } = require('./middleware');

const router = express.Router();

router.get('/download', requireEntitlement, (req, res) => {
  const releases = sync.availableReleases();
  const def = sync.defaultRelease();
  res.render('download', {
    title: 'Download Oversight',
    releases,
    def,
    justPurchased: req.query.purchase === 'success',
    syncedAt: sync.getStatus().lastRunUtc
  });
});

router.get('/download/file', requireEntitlement, (req, res) => {
  const version = String(req.query.version || '');
  const channel = String(req.query.channel || '');
  const resolved = sync.resolveFile(version, channel);
  if (!resolved) {
    return res.status(404).render('error', { title: 'Not found', message: 'That build is not available. Try another version.' });
  }
  if (resolved.sha256) res.setHeader('X-Installer-SHA256', resolved.sha256);
  res.setHeader('Content-Type', 'application/octet-stream');
  res.download(resolved.abs, resolved.name);
});

router.get('/account', requireAuth, (req, res) => {
  res.render('account', {
    title: 'Your account',
    purchases: db.purchasesForUser(req.user.id),
    entitled: db.hasPaid(req.user.id),
    price: config.settings
  });
});

module.exports = router;
