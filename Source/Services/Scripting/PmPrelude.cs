namespace PayloadPanda.Services.Scripting;

/// <summary>
/// JavaScript run before every user script. It builds the <c>pm</c> object (a subset of
/// Postman's) from the JSON context C# passes in as <c>__ctxJson</c>, and exposes
/// <c>__collect()</c>, which returns everything the script produced as JSON. Nothing from
/// .NET is visible to scripts: the two JSON strings are the whole interface.
/// </summary>
internal static class PmPrelude
{
    public const string Source = """
        (function (global) {
          'use strict';
          var ctx = JSON.parse(global.__ctxJson);
          // The variable layers, highest precedence first when read through pm.variables.
          var S = ctx.scopes;
          var layers = [S.locals, S.environment, S.collection, S.globals];
          var out = { tests: [], console: [], scopes: S, requestHeaders: ctx.request.headers };
          var own = function (o, k) { return Object.prototype.hasOwnProperty.call(o, k); };

          // ---- console ----
          function show(v) {
            if (typeof v === 'string') return v;
            if (v === undefined) return 'undefined';
            if (typeof v === 'function') return '[Function]';
            if (v instanceof Error) return String(v);
            try { var s = JSON.stringify(v); return s === undefined ? String(v) : s; } catch (e) { return String(v); }
          }
          function logger(level) {
            return function () { out.console.push({ level: level, text: Array.prototype.map.call(arguments, show).join(' ') }); };
          }
          global.console = { log: logger('log'), info: logger('info'), debug: logger('log'), warn: logger('warn'), error: logger('error') };

          // ---- variables ----
          function lookup(name) {
            for (var i = 0; i < layers.length; i++) if (own(layers[i], name)) return layers[i][name];
            return undefined;
          }
          function replaceIn(text) {
            return String(text).replace(/\{\{\s*([^{}]+?)\s*\}\}/g, function (all, name) { var v = lookup(name); return v === undefined ? all : v; });
          }
          function toText(v) {
            if (v === undefined || v === null) return '';
            if (typeof v === 'string') return v;
            return typeof v === 'object' ? JSON.stringify(v) : String(v);
          }
          // One stored layer: the collection's variables, the active environment's, or the globals.
          function scope(vars) {
            return {
              get: function (k) { return own(vars, k) ? vars[k] : undefined; },
              set: function (k, v) { vars[String(k)] = toText(v); },
              unset: function (k) { delete vars[k]; },
              has: function (k) { return own(vars, k); },
              clear: function () { Object.keys(vars).forEach(function (k) { delete vars[k]; }); },
              toObject: function () { return JSON.parse(JSON.stringify(vars)); },
              replaceIn: replaceIn
            };
          }
          // pm.variables reads through every layer; what it sets is local to this send.
          var resolved = {
            get: lookup,
            has: function (k) { return lookup(k) !== undefined; },
            set: function (k, v) { S.locals[String(k)] = toText(v); },
            unset: function (k) { delete S.locals[k]; },
            toObject: function () {
              var all = {};
              for (var i = layers.length - 1; i >= 0; i--) Object.keys(layers[i]).forEach(function (k) { all[k] = layers[i][k]; });
              return all;
            },
            replaceIn: replaceIn
          };
          var environment = scope(S.environment);
          environment.name = ctx.environmentName === null ? undefined : ctx.environmentName;

          // ---- headers ----
          function headerList(list, writable) {
            function find(name) {
              name = String(name).toLowerCase();
              for (var i = 0; i < list.length; i++) if (list[i].key.toLowerCase() === name) return i;
              return -1;
            }
            var api = {
              get: function (name) { var i = find(name); return i < 0 ? undefined : list[i].value; },
              has: function (name) { return find(name) >= 0; },
              all: function () { return list.map(function (h) { return { key: h.key, value: h.value }; }); },
              each: function (fn) { list.forEach(function (h) { fn({ key: h.key, value: h.value }); }); },
              toObject: function () { var o = {}; list.forEach(function (h) { o[h.key] = h.value; }); return o; }
            };
            if (writable) {
              var entry = function (h, v) { return typeof h === 'object' && h !== null ? { key: String(h.key), value: toText(h.value) } : { key: String(h), value: toText(v) }; };
              api.add = function (h, v) { list.push(entry(h, v)); };
              api.upsert = function (h, v) { var e = entry(h, v), i = find(e.key); if (i < 0) list.push(e); else list[i] = e; };
              api.remove = function (name) { for (var i = find(name); i >= 0; i = find(name)) list.splice(i, 1); };
            }
            return api;
          }

          // ---- assertions (a chai-style subset) ----
          function AssertionError(message) { this.name = 'AssertionError'; this.message = message; }
          AssertionError.prototype = Object.create(Error.prototype);
          AssertionError.prototype.constructor = AssertionError;

          function deepEqual(a, b) {
            if (a === b) return true;
            if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) return a !== a && b !== b;
            if (Array.isArray(a) !== Array.isArray(b)) return false;
            var ka = Object.keys(a), kb = Object.keys(b);
            if (ka.length !== kb.length) return false;
            return ka.every(function (k) { return own(b, k) && deepEqual(a[k], b[k]); });
          }
          function typeOf(v) { return v === null ? 'null' : Array.isArray(v) ? 'array' : v instanceof RegExp ? 'regexp' : v instanceof Date ? 'date' : typeof v; }

          function Assertion(value, message) { this._v = value; this._msg = message; this._not = false; this._deep = false; }
          var P = Assertion.prototype;
          P._assert = function (ok, what) {
            if (this._not ? ok : !ok)
              throw new AssertionError((this._msg ? this._msg + ': ' : '') + 'expected ' + show(this._v) + (this._not ? ' not ' : ' ') + what);
            return this;
          };
          ['to', 'be', 'been', 'is', 'that', 'which', 'and', 'has', 'have', 'with', 'at', 'of', 'same', 'does', 'still', 'also'].forEach(function (word) {
            Object.defineProperty(P, word, { get: function () { return this; } });
          });
          Object.defineProperty(P, 'not', { get: function () { this._not = !this._not; return this; } });
          Object.defineProperty(P, 'deep', { get: function () { this._deep = true; return this; } });
          function check(name, test, what) { Object.defineProperty(P, name, { get: function () { return this._assert(test(this._v), what); } }); }
          check('true', function (v) { return v === true; }, 'to be true');
          check('false', function (v) { return v === false; }, 'to be false');
          check('null', function (v) { return v === null; }, 'to be null');
          check('undefined', function (v) { return v === undefined; }, 'to be undefined');
          check('NaN', function (v) { return v !== v; }, 'to be NaN');
          check('ok', function (v) { return !!v; }, 'to be truthy');
          check('exist', function (v) { return v !== null && v !== undefined; }, 'to exist');
          check('empty', function (v) { return v === null || v === undefined ? false : typeof v === 'object' && !Array.isArray(v) ? Object.keys(v).length === 0 : v.length === 0; }, 'to be empty');

          P.equal = P.equals = P.eq = function (x) { return this._assert(this._deep ? deepEqual(this._v, x) : this._v === x, 'to equal ' + show(x)); };
          P.eql = P.eqls = function (x) { return this._assert(deepEqual(this._v, x), 'to deeply equal ' + show(x)); };
          P.a = P.an = function (type) { return this._assert(typeOf(this._v) === String(type).toLowerCase(), 'to be a ' + type); };
          P.include = P.includes = P.contain = P.contains = function (x) {
            var v = this._v, ok;
            if (typeof v === 'string') ok = v.indexOf(x) >= 0;
            else if (Array.isArray(v)) ok = v.some(function (e) { return deepEqual(e, x); });
            else if (v && typeof v === 'object' && x && typeof x === 'object') ok = Object.keys(x).every(function (k) { return own(v, k) && deepEqual(v[k], x[k]); });
            else ok = false;
            return this._assert(ok, 'to include ' + show(x));
          };
          P.string = function (x) { return this._assert(typeof this._v === 'string' && this._v.indexOf(x) >= 0, 'to contain ' + show(x)); };
          P.property = function (name, value) {
            var v = this._v, has = v !== null && v !== undefined && (name in Object(v));
            if (arguments.length > 1) return this._assert(has && deepEqual(v[name], value), 'to have property ' + show(name) + ' of ' + show(value));
            return this._assert(has, 'to have property ' + show(name));
          };
          P.keys = function () {
            var names = arguments.length === 1 && Array.isArray(arguments[0]) ? arguments[0] : Array.prototype.slice.call(arguments), v = this._v;
            return this._assert(v !== null && v !== undefined && names.every(function (k) { return own(v, k); }), 'to have keys ' + show(names));
          };
          P.lengthOf = P.length = function (n) { var v = this._v; return this._assert(v !== null && v !== undefined && v.length === n, 'to have length ' + n); };
          P.above = P.gt = P.greaterThan = function (n) { return this._assert(this._v > n, 'to be above ' + n); };
          P.below = P.lt = P.lessThan = function (n) { return this._assert(this._v < n, 'to be below ' + n); };
          P.least = P.gte = function (n) { return this._assert(this._v >= n, 'to be at least ' + n); };
          P.most = P.lte = function (n) { return this._assert(this._v <= n, 'to be at most ' + n); };
          P.within = function (a, b) { return this._assert(this._v >= a && this._v <= b, 'to be within ' + a + '..' + b); };
          P.match = P.matches = function (re) { return this._assert(re.test(this._v), 'to match ' + String(re)); };
          P.oneOf = function (list) { var v = this._v; return this._assert(list.some(function (e) { return deepEqual(e, v); }), 'to be one of ' + show(list)); };

          // ---- pm ----
          var unsupported = function (what) { return function () { throw new Error(what + ' is not supported in PayloadPanda'); }; };
          var pm = {
            info: ctx.info,
            variables: resolved,
            collectionVariables: scope(S.collection),
            environment: environment,
            globals: scope(S.globals),
            expect: function (value, message) { return new Assertion(value, message); },
            test: function (name, fn) {
              try { fn(); out.tests.push({ name: String(name), passed: true }); }
              catch (e) { out.tests.push({ name: String(name), passed: false, error: e && e.message ? e.message : String(e) }); }
            },
            sendRequest: unsupported('pm.sendRequest'),
            request: {
              method: ctx.request.method,
              url: { toString: function () { return ctx.request.url; }, getRaw: function () { return ctx.request.url; } },
              headers: headerList(ctx.request.headers, true),
              body: { raw: ctx.request.body, toString: function () { return ctx.request.body; } }
            }
          };
          pm.test.skip = function (name) { out.tests.push({ name: String(name), passed: true, skipped: true }); };
          Object.defineProperty(pm, 'cookies', { get: unsupported('pm.cookies') });

          var res = ctx.response;
          if (res) {
            var statusCheck = function (test, what) { return function () { if (!test(res.code)) throw new AssertionError('expected response to ' + what + ' but got ' + res.code); }; };
            var be = {};
            Object.defineProperty(be, 'ok', { get: statusCheck(function (c) { return c >= 200 && c < 300; }, 'be ok') });
            Object.defineProperty(be, 'success', { get: statusCheck(function (c) { return c >= 200 && c < 300; }, 'be a success') });
            Object.defineProperty(be, 'error', { get: statusCheck(function (c) { return c >= 400; }, 'be an error') });
            Object.defineProperty(be, 'clientError', { get: statusCheck(function (c) { return c >= 400 && c < 500; }, 'be a client error') });
            Object.defineProperty(be, 'serverError', { get: statusCheck(function (c) { return c >= 500; }, 'be a server error') });
            var responseHeaders = headerList(res.headers, false);
            pm.response = {
              code: res.code,
              status: res.status,
              responseTime: res.responseTime,
              responseSize: res.size,
              headers: responseHeaders,
              text: function () { return res.body; },
              json: function () { return JSON.parse(res.body); },
              to: {
                be: be,
                have: {
                  status: function (expected) {
                    var ok = typeof expected === 'number' ? res.code === expected : res.status === expected;
                    if (!ok) throw new AssertionError('expected response to have status ' + show(expected) + ' but got ' + res.code + ' ' + res.status);
                  },
                  header: function (name) { if (!responseHeaders.has(name)) throw new AssertionError('expected response to have header ' + name); },
                  body: function (text) { if (res.body !== text) throw new AssertionError('expected response body to equal ' + show(text)); },
                  jsonBody: function () { try { JSON.parse(res.body); } catch (e) { throw new AssertionError('expected response body to be JSON'); } }
                }
              }
            };
          }

          // ---- base64 ----
          var B64 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
          global.btoa = function (input) {
            var s = String(input), o = '';
            for (var i = 0; i < s.length; i += 3) {
              var a = s.charCodeAt(i), b = s.charCodeAt(i + 1), c = s.charCodeAt(i + 2);
              if (a > 255 || b > 255 || c > 255) throw new Error('btoa: the string contains characters outside Latin1');
              o += B64[a >> 2] + B64[((a & 3) << 4) | (b >> 4 || 0)] + (isNaN(b) ? '=' : B64[((b & 15) << 2) | (c >> 6 || 0)]) + (isNaN(c) ? '=' : B64[c & 63]);
            }
            return o;
          };
          global.atob = function (input) {
            var s = String(input).replace(/[\s=]+/g, ''), o = '', bits = 0, acc = 0;
            for (var i = 0; i < s.length; i++) {
              var v = B64.indexOf(s[i]);
              if (v < 0) throw new Error('atob: the string is not valid base64');
              acc = (acc << 6) | v; bits += 6;
              if (bits >= 8) { bits -= 8; o += String.fromCharCode((acc >> bits) & 255); }
            }
            return o;
          };

          global.pm = pm;
          global.postman = { setNextRequest: unsupported('postman.setNextRequest') };
          global.require = function (name) { throw new Error("require('" + name + "') is not supported in PayloadPanda"); };
          global.__collect = function () { return JSON.stringify(out); };
        })(globalThis);
        """;
}
