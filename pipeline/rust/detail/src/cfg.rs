//! Read-only access to the rules (a ``serde_json::Value`` tree, order of keys kept) by dotted path.

use serde_json::Value;

static NULL: Value = Value::Null;

#[derive(Clone, Copy)]
pub struct Cfg<'a>(pub &'a Value);

impl<'a> Cfg<'a> {
    pub fn at(&self, key: &str) -> Cfg<'a> {
        Cfg(self.0.get(key).unwrap_or(&NULL))
    }

    /// ``cfg.p("a.b.c")``.
    pub fn p(&self, path: &str) -> Cfg<'a> {
        let mut c = *self;
        for k in path.split('.') {
            c = c.at(k);
        }
        c
    }

    pub fn is_null(&self) -> bool {
        self.0.is_null()
    }

    pub fn has(&self, key: &str) -> bool {
        self.0.get(key).is_some_and(|v| !v.is_null())
    }

    pub fn f(&self) -> f64 {
        match self.0 {
            Value::Number(n) => n.as_f64().unwrap_or(0.0),
            Value::Bool(b) => *b as i64 as f64,
            Value::String(s) => s.parse().unwrap_or(0.0),
            _ => 0.0,
        }
    }

    pub fn fo(&self, default: f64) -> f64 {
        if self.0.is_null() {
            default
        } else {
            self.f()
        }
    }

    pub fn i(&self) -> i64 {
        match self.0 {
            Value::Number(n) => n.as_i64().unwrap_or_else(|| n.as_f64().unwrap_or(0.0) as i64),
            _ => 0,
        }
    }

    pub fn s(&self) -> &'a str {
        self.0.as_str().unwrap_or("")
    }

    pub fn arr(&self) -> Vec<Cfg<'a>> {
        self.0.as_array().map(|a| a.iter().map(Cfg).collect()).unwrap_or_default()
    }

    pub fn fvec(&self) -> Vec<f64> {
        self.arr().iter().map(|c| c.f()).collect()
    }

    pub fn f2(&self) -> [f64; 2] {
        let v = self.fvec();
        [v.first().copied().unwrap_or(0.0), v.get(1).copied().unwrap_or(0.0)]
    }

    pub fn svec(&self) -> Vec<&'a str> {
        self.arr().iter().map(|c| c.s()).collect()
    }

    /// Entries of an object in file order.
    pub fn entries(&self) -> Vec<(&'a str, Cfg<'a>)> {
        self.0.as_object().map(|o| o.iter().map(|(k, v)| (k.as_str(), Cfg(v))).collect()).unwrap_or_default()
    }

    /// ``[[name, share], ...]`` pairs.
    pub fn shares(&self) -> Vec<(&'a str, f64)> {
        self.arr().iter().map(|e| {
            let p = e.arr();
            (p[0].s(), p[1].f())
        }).collect()
    }
}
