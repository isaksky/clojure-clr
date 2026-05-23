(ns clojure.samples.sudoku-solver
  (:require [clojure.samples.sudoku-puzzles :as puzzles]
            [clojure.set :as set]))

(def digits #{1 2 3 4 5 6 7 8 9})
(def cell-indexes (range 81))

(def row-units
  (for [row (range 9)]
    (for [col (range 9)]
      (+ (* row 9) col))))

(def column-units
  (for [col (range 9)]
    (for [row (range 9)]
      (+ (* row 9) col))))

(def box-units
  (for [box-row (range 0 9 3)
        box-col (range 0 9 3)]
    (for [row (range box-row (+ box-row 3))
          col (range box-col (+ box-col 3))]
      (+ (* row 9) col))))

(def all-units (concat row-units column-units box-units))

(defn row [index]
  (quot index 9))

(defn column [index]
  (rem index 9))

(defn box-start [n]
  (* 3 (quot n 3)))

(defn peer-indexes [index]
  (let [r (row index)
        c (column index)
        box-row (box-start r)
        box-col (box-start c)]
    (disj (set (concat (for [col (range 9)]
                         (+ (* r 9) col))
                       (for [row (range 9)]
                         (+ (* row 9) c))
                       (for [row (range box-row (+ box-row 3))
                             col (range box-col (+ box-col 3))]
                         (+ (* row 9) col))))
          index)))

(def peers
  (vec (map peer-indexes cell-indexes)))

(defn parse-cell [ch]
  (let [n (- (int ch) (int \0))]
    (if (<= 0 n 9)
      n
      (throw (Exception. (str "Sudoku cells must be digits 0-9, got: " ch))))))

(defn parse-board [rows]
  (let [board (vec (mapcat #(map parse-cell %) rows))]
    (when-not (= 81 (count board))
      (throw (Exception. (str "Sudoku board must contain 81 cells, got: "
                              (count board)))))
    board))

(defn board->rows [board]
  (map #(apply str %) (partition 9 board)))

(defn filled-values [values]
  (remove zero? values))

(defn valid-unit? [values]
  (let [filled (filled-values values)]
    (= (count filled) (count (distinct filled)))))

(defn valid-board? [board]
  (every? valid-unit? (map #(map board %) all-units)))

(defn solved? [board]
  (not-any? zero? board))

(defn used-values [board index]
  (set (filled-values (map board (peers index)))))

(defn candidate-values [board index]
  (if (zero? (board index))
    (set/difference digits (used-values board index))
    #{(board index)}))

(defn best-empty-cell [board]
  (apply min-key
         (fn [[_ candidates]] (count candidates))
         (for [index cell-indexes
               :when (zero? (board index))]
           [index (candidate-values board index)])))

(defn solve [board]
  (when (valid-board? board)
    (if (solved? board)
      board
      (let [[index candidates] (best-empty-cell board)]
        (when (seq candidates)
          (some #(solve (assoc board index %)) candidates))))))

(defn solve-puzzle [puzzle]
  (let [board (parse-board (:rows puzzle))
        solution (solve board)]
    (when-not solution
      (throw (Exception. (str "Puzzle has no solution: " (:id puzzle)))))
    (assoc puzzle
           :solution (board->rows solution)
           :top-left (reduce #(+ (* %1 10) %2) 0 (take 3 solution)))))

(defn solve-project-euler-puzzles []
  (map solve-puzzle puzzles/project-euler-puzzles))

(defn print-grid [rows]
  (doseq [row rows]
    (println row)))

(defn demo []
  (let [solved-puzzles (doall (solve-project-euler-puzzles))]
    (println "Project Euler source:" puzzles/project-euler-source)
    (doseq [puzzle solved-puzzles]
      (println)
      (println (:id puzzle))
      (println "Puzzle:")
      (print-grid (:rows puzzle))
      (println "Solution:")
      (print-grid (:solution puzzle))
      (println "Top-left value:" (:top-left puzzle)))
    (println)
    (println "Top-left sum:"
             (reduce + (map :top-left solved-puzzles)))))

(demo)
