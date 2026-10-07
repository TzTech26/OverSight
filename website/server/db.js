'use strict';
/**
 * SQLite data layer (better-sqlite3, synchronous — fine for this scale).
 * Tables: users, purchases, login_tokens. Sessions are stored separately by
 * connect-sqlite3 in the same data/ dir.
 */
const path = require('path');
const crypto = require('crypto');
const Database = require('better-sqlite3');
const { DATA_DIR } = require('./config');

const db = new Database(path.join(DATA_DIR, 'oversight.db'));
db.pragma('journal_mode = WAL');
db.pragma('foreign_keys = ON');

db.exec(`
CREATE TABLE IF NOT EXISTS users (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  email       TEXT NOT NULL UNIQUE,
  name        TEXT,
  google_id   TEXT UNIQUE,
  is_admin    INTEGER NOT NULL DEFAULT 0,
  created_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS purchases (
  id                 INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id            INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  stripe_session_id  TEXT UNIQUE,
  stripe_payment_id  TEXT,
  amount_cents       INTEGER,
  currency           TEXT,
  status             TEXT NOT NULL DEFAULT 'pending', -- pending | paid | comped | refunded
  source             TEXT NOT NULL DEFAULT 'stripe',  -- stripe | admin
  created_at         TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS login_tokens (
  token       TEXT PRIMARY KEY,
  email       TEXT NOT NULL,
  expires_at  INTEGER NOT NULL,          -- epoch ms
  used        INTEGER NOT NULL DEFAULT 0,
  created_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_purchases_user ON purchases(user_id);
`);

const norm = (e) => String(e || '').trim().toLowerCase();

const stmts = {
  userByEmail: db.prepare('SELECT * FROM users WHERE email = ?'),
  userById: db.prepare('SELECT * FROM users WHERE id = ?'),
  userByGoogle: db.prepare('SELECT * FROM users WHERE google_id = ?'),
  insertUser: db.prepare(
    'INSERT INTO users (email, name, google_id, is_admin) VALUES (@email, @name, @google_id, @is_admin)'
  ),
  setGoogleId: db.prepare('UPDATE users SET google_id = ?, name = COALESCE(name, ?) WHERE id = ?'),
  setAdmin: db.prepare('UPDATE users SET is_admin = ? WHERE id = ?'),
  allUsers: db.prepare('SELECT * FROM users ORDER BY created_at DESC'),

  paidForUser: db.prepare(
    "SELECT COUNT(*) AS n FROM purchases WHERE user_id = ? AND status IN ('paid','comped')"
  ),
  purchasesForUser: db.prepare('SELECT * FROM purchases WHERE user_id = ? ORDER BY created_at DESC'),
  insertPurchase: db.prepare(
    `INSERT INTO purchases (user_id, stripe_session_id, stripe_payment_id, amount_cents, currency, status, source)
     VALUES (@user_id, @stripe_session_id, @stripe_payment_id, @amount_cents, @currency, @status, @source)`
  ),
  purchaseBySession: db.prepare('SELECT * FROM purchases WHERE stripe_session_id = ?'),
  markPaid: db.prepare(
    "UPDATE purchases SET status = 'paid', stripe_payment_id = ? WHERE stripe_session_id = ?"
  ),
  recentPurchases: db.prepare(
    `SELECT p.*, u.email FROM purchases p JOIN users u ON u.id = p.user_id
     ORDER BY p.created_at DESC LIMIT 100`
  ),

  insertToken: db.prepare(
    'INSERT INTO login_tokens (token, email, expires_at) VALUES (?, ?, ?)'
  ),
  getToken: db.prepare('SELECT * FROM login_tokens WHERE token = ?'),
  useToken: db.prepare('UPDATE login_tokens SET used = 1 WHERE token = ?'),
  purgeTokens: db.prepare('DELETE FROM login_tokens WHERE expires_at < ?')
};

module.exports = {
  db,

  getUserById: (id) => stmts.userById.get(id),
  getUserByEmail: (email) => stmts.userByEmail.get(norm(email)),

  /** Find or create a user by email; optionally attach a Google id/name. */
  upsertUser({ email, name = null, googleId = null }, adminEmails = []) {
    email = norm(email);
    let user = stmts.userByEmail.get(email);
    const isAdmin = adminEmails.includes(email) ? 1 : 0;
    if (!user) {
      const info = stmts.insertUser.run({ email, name, google_id: googleId, is_admin: isAdmin });
      user = stmts.userById.get(info.lastInsertRowid);
    } else {
      if (googleId && !user.google_id) { stmts.setGoogleId.run(googleId, name, user.id); }
      if (isAdmin && !user.is_admin) { stmts.setAdmin.run(1, user.id); user.is_admin = 1; }
      user = stmts.userById.get(user.id);
    }
    return user;
  },

  setAdmin: (userId, on) => stmts.setAdmin.run(on ? 1 : 0, userId),
  allUsers: () => stmts.allUsers.all(),

  hasPaid: (userId) => stmts.paidForUser.get(userId).n > 0,
  purchasesForUser: (userId) => stmts.purchasesForUser.all(userId),
  recentPurchases: () => stmts.recentPurchases.all(),
  purchaseBySession: (sid) => stmts.purchaseBySession.get(sid),

  createPendingPurchase({ userId, sessionId, amountCents, currency }) {
    return stmts.insertPurchase.run({
      user_id: userId, stripe_session_id: sessionId, stripe_payment_id: null,
      amount_cents: amountCents, currency, status: 'pending', source: 'stripe'
    });
  },
  markSessionPaid: (sessionId, paymentId) => stmts.markPaid.run(paymentId || null, sessionId),

  compUser({ userId }) {
    return stmts.insertPurchase.run({
      user_id: userId, stripe_session_id: `comp_${crypto.randomUUID()}`, stripe_payment_id: null,
      amount_cents: 0, currency: 'usd', status: 'comped', source: 'admin'
    });
  },

  // --- magic-link tokens ---
  createLoginToken(email, ttlMs = 15 * 60 * 1000) {
    const token = crypto.randomBytes(32).toString('hex');
    stmts.insertToken.run(token, norm(email), Date.now() + ttlMs);
    return token;
  },
  consumeLoginToken(token) {
    stmts.purgeTokens.run(Date.now());
    const row = stmts.getToken.get(token);
    if (!row || row.used || row.expires_at < Date.now()) return null;
    stmts.useToken.run(token);
    return row.email;
  }
};
