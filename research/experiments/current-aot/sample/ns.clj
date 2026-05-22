(ns sample.ns)

(def answer 41)

(defn inc-answer []
  (inc answer))

(def invoked (inc-answer))

(let [x 5]
  (+ x invoked))

(def after-let :loaded)
