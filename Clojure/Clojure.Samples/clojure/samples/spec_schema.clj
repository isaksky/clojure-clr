; A clojure.spec sample modeled after common JSON Schema product/order examples.

(ns clojure.samples.spec-schema
  (:require [clojure.spec.alpha :as s]
            [clojure.spec.test.alpha :as st]))

(defn non-empty-string? [x]
  (and (string? x) (pos? (.Length ^String x))))

(defn non-negative-number? [x]
  (and (number? x) (not (neg? x))))

(defn positive-number? [x]
  (and (number? x) (pos? x)))

(defn non-negative-int? [x]
  (and (integer? x) (not (neg? x))))

(s/def ::product-id non-negative-int?)
(s/def ::product-name non-empty-string?)
(s/def ::price non-negative-number?)
(s/def ::currency #{"EUR" "JPY" "USD"})
(s/def ::tag non-empty-string?)
(s/def ::tags (s/coll-of ::tag :kind vector? :min-count 1 :distinct true))

(s/def ::length positive-number?)
(s/def ::width positive-number?)
(s/def ::height positive-number?)
(s/def ::dimensions (s/keys :req-un [::length ::width ::height]))
(s/def ::warehouse-location
  (s/and string? #(re-matches #"[A-Z]{2}-[0-9]{3}" %)))

(s/def ::product
  (s/keys :req-un [::product-id ::product-name ::price]
          :opt-un [::currency ::tags ::dimensions ::warehouse-location]))

(s/def ::customer-id non-empty-string?)
(s/def ::email (s/and string? #(re-matches #"^[^@]+@[^@]+[.][^@]+$" %)))
(s/def ::phone (s/and string? #(re-matches #"^[0-9]{3}-[0-9]{3}-[0-9]{4}$" %)))
(s/def ::contact (s/or :email ::email :phone ::phone))

(s/def ::quantity (s/and integer? pos?))
(s/def ::note non-empty-string?)
(s/def ::line-item (s/keys :req-un [::product-id ::quantity] :opt-un [::note]))
(s/def ::items (s/coll-of ::line-item :kind vector? :min-count 1))

(s/def ::order-id non-empty-string?)
(s/def ::customer (s/keys :req-un [::customer-id ::contact]))
(s/def ::coupon non-empty-string?)
(s/def ::order
  (s/keys :req-un [::order-id ::customer ::items]
          :opt-un [::coupon]))

(s/def ::catalog (s/map-of ::product-id ::product :min-count 1))

(def sample-product
  {:product-id 1001
   :product-name "Widget"
   :price 12.5
   :currency "USD"
   :tags ["hardware" "sale"]
   :dimensions {:length 7.5
                :width 3.0
                :height 1.25}
   :warehouse-location "CO-042"})

(def sample-catalog
  {1001 sample-product
   1002 {:product-id 1002
         :product-name "Cable"
         :price 4.25
         :currency "USD"
         :tags ["accessory"]
         :warehouse-location "NY-117"}})

(def sample-order
  {:order-id "SO-10001"
   :customer {:customer-id "C-48"
              :contact "ops@example.com"}
   :items [{:product-id 1001
            :quantity 2}
           {:product-id 1002
            :quantity 3
            :note "Ship with widget"}]})

(def invalid-product
  {:product-id "1003"
   :product-name ""
   :price -1
   :tags ["sale" "sale"]
   :dimensions {:length 0
                :width 4
                :height 2}
   :warehouse-location "north"})

(def invalid-order
  {:order-id "SO-10002"
   :customer {:customer-id ""
              :contact "not-a-contact"}
   :items [{:product-id "1001"
            :quantity 0}
           {:quantity 2}]})

(defn line-total [line catalog]
  (* (:quantity line) (:price (get catalog (:product-id line)))))

(s/fdef line-total
  :args (s/cat :line ::line-item :catalog ::catalog)
  :ret ::price)

(defn explain-summary [spec value]
  (let [data (s/explain-data spec value)]
    (if data
      (mapv #(select-keys % [:path :pred :val :via :in])
            (::s/problems data))
      [])))

(defn instrumented-failure []
  (try
    (line-total {:product-id "1001" :quantity 2} sample-catalog)
    :not-thrown
    (catch clojure.lang.ExceptionInfo ex
      (:clojure.spec.alpha/failure (ex-data ex)))))

(defn run []
  (st/instrument `line-total)
  {:product-valid? (s/valid? ::product sample-product)
   :catalog-valid? (s/valid? ::catalog sample-catalog)
   :order-valid? (s/valid? ::order sample-order)
   :email-contact (s/conform ::contact "ops@example.com")
   :phone-contact (s/conform ::contact "303-555-0100")
   :invalid-product (explain-summary ::product invalid-product)
   :invalid-order (explain-summary ::order invalid-order)
   :line-total (line-total {:product-id 1001 :quantity 2} sample-catalog)
   :instrumented-failure (instrumented-failure)})

(def sample-results (run))
