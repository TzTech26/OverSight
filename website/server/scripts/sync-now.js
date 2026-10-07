'use strict';
// One-off: pull latest release files from the NAS right now, then exit.
const sync = require('../sync');
const st = sync.runSync();
console.log(st.ok ? 'OK: ' + st.message : 'FAILED: ' + st.message);
process.exit(st.ok ? 0 : 1);
