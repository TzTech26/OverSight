'use strict';
/** Auth/entitlement guards + view locals. */
const db = require('./db');

/** Attach the current user (if any) to req/res for every request. */
function loadUser(req, res, next) {
  const uid = req.session && req.session.userId;
  req.user = uid ? db.getUserById(uid) : null;
  res.locals.user = req.user || null;
  res.locals.hasPaid = req.user ? db.hasPaid(req.user.id) : false;
  res.locals.isAdmin = !!(req.user && req.user.is_admin);
  res.locals.currentPath = req.path;
  return next();
}

function requireAuth(req, res, next) {
  if (req.user) return next();
  const next_ = encodeURIComponent(req.originalUrl);
  return res.redirect(`/login?next=${next_}`);
}

function requireEntitlement(req, res, next) {
  if (!req.user) return res.redirect(`/login?next=${encodeURIComponent(req.originalUrl)}`);
  if (db.hasPaid(req.user.id)) return next();
  return res.redirect('/#pricing');
}

function requireAdmin(req, res, next) {
  if (req.user && req.user.is_admin) return next();
  return res.status(403).render('error', { title: 'Forbidden', message: 'Admins only.' });
}

module.exports = { loadUser, requireAuth, requireEntitlement, requireAdmin };
