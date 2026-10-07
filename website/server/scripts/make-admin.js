'use strict';
// Usage: node server/scripts/make-admin.js you@example.com
// Grants admin to an existing user (they must have signed in at least once).
const db = require('../db');
const email = process.argv[2];
if (!email) { console.error('Usage: node server/scripts/make-admin.js <email>'); process.exit(1); }
const user = db.getUserByEmail(email);
if (!user) { console.error(`No user with email ${email}. Have them sign in once first.`); process.exit(1); }
db.setAdmin(user.id, true);
console.log(`OK: ${email} is now an admin.`);
