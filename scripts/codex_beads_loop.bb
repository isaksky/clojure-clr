#!/usr/bin/env bb

(ns codex-beads-loop
  (:require
   [cheshire.core :as json]
   [clojure.java.io :as io]
   [clojure.java.shell :as shell]
   [clojure.string :as str])
  (:import
   [java.lang ProcessBuilder$Redirect]))

(def work-prompt
  (str "Orient yourself with AGENTS.md and research/Goal.md. "
       "Use beads (`br`) for task tracking: first check for in-progress beads, "
       "then use `br ready` to find the next actionable bead if none are in progress. "
       "Work on one bead in this run: claim it, implement it, close it when complete. "
       "If you discover any new tasks as you are doing this bead, or the current one gets too big, create new beads to track new tasks. "
       "run `br sync --flush-only`, and commit all uncommitted changes to git. "
       ))

(def commit-prompt
  (str "There are still uncommitted changes after the previous Codex run. "
       "Do not start a new beads task. Run `br sync --flush-only` if needed, "
       "inspect the dirty tree, and commit all uncommitted changes to git now. "
       "If there is nothing to commit, explain why and exit."))

(def usage
  (str "Usage: scripts/codex_beads_loop.bb [--dry-run] [--help]\n\n"
       "Runs `codex exec` repeatedly while beads has ready or in-progress work.\n\n"
       "Environment:\n"
       "  CODEX_BIN                         Codex executable (default: codex)\n"
       "  CODEX_MODEL                       Optional model passed as `-m`\n"
       "  CODEX_PROFILE                     Optional profile passed as `-p`\n"
       "  CODEX_BYPASS_APPROVALS            Use danger-full-access automation (default: true)\n"
       "  BEADS_BIN                         Beads executable (default: br)\n"
       "  BEADS_CODEX_MAX_ITERATIONS        Loop guard (default: 100)\n"
       "  BEADS_CODEX_MAX_FAILURES          Consecutive Codex failure guard (default: 3)\n"
       "  BEADS_CODEX_MAX_STALLED_ITERATIONS No-progress guard (default: 2)\n"
       "  BEADS_CODEX_MAX_COMMIT_PROMPTS    Commit retry guard (default: 3)\n"))

(defn env-value
  [name default-value]
  (let [value (System/getenv name)]
    (if (str/blank? value)
      default-value
      value)))

(defn parse-int-env
  [name default-value]
  (let [raw-value (env-value name (str default-value))]
    (try
      (Integer/parseInt raw-value)
      (catch NumberFormatException exception
        (throw (ex-info (str name " must be an integer, got: " raw-value)
                        {:env name :value raw-value}
                        exception))))))

(defn truthy-env?
  [name default-value]
  (let [raw-value (str/lower-case (env-value name default-value))]
    (not (#{"0" "false" "no" "off"} raw-value))))

(defn beads-bin
  []
  (env-value "BEADS_BIN" "br"))

(defn shell-quote
  [value]
  (if (re-find #"\s" value)
    (str "'" (str/replace value #"'" "'\"'\"'") "'")
    value))

(defn run-capture!
  [repo-root args]
  (let [result (apply shell/sh (concat args [:dir repo-root]))]
    (when-not (zero? (:exit result))
      (throw (ex-info (str (str/join " " args) " failed")
                      {:exit (:exit result)
                       :out (:out result)
                       :err (:err result)})))
    (str/trim (:out result))))

(defn run-git
  [repo-root & args]
  (run-capture! repo-root (into ["git" "-C" repo-root] args)))

(defn git-root
  []
  (let [result (shell/sh "git" "rev-parse" "--show-toplevel")]
    (when-not (zero? (:exit result))
      (throw (ex-info "This script must run inside a git repository."
                      {:exit (:exit result)
                       :out (:out result)
                       :err (:err result)})))
    (str/trim (:out result))))

(defn git-head
  [repo-root]
  (run-git repo-root "rev-parse" "HEAD"))

(defn git-status
  [repo-root]
  (run-git repo-root "status" "--porcelain=v1"))

(defn git-clean?
  [repo-root]
  (str/blank? (git-status repo-root)))

(defn parse-json-output
  [output]
  (if (str/blank? output)
    []
    (json/parse-string output true)))

(defn br-json
  [repo-root & args]
  (parse-json-output
   (run-capture! repo-root (into [(beads-bin)] args))))

(defn issues-json
  [repo-root & args]
  (let [result (apply br-json repo-root args)]
    (cond
      (vector? result) result
      (sequential? result) (vec result)
      (map? result) (vec (:issues result))
      :else (throw (ex-info "Unexpected beads JSON output"
                            {:result result
                             :args args})))))

(defn ready-issues
  [repo-root]
  (issues-json repo-root "ready" "--json" "--limit" "0" "--no-color"))

(defn in-progress-issues
  [repo-root]
  (issues-json repo-root "list" "--status" "in_progress" "--json" "--limit" "0" "--no-color"))

(defn active-state
  [repo-root]
  {:ready (ready-issues repo-root)
   :in-progress (in-progress-issues repo-root)})

(defn issue-label
  [issue]
  (str (:id issue) "(P" (:priority issue) " " (:issue_type issue) ")"))

(defn summarize-issue-group
  [label issues]
  (if (seq issues)
    (str (count issues) " " label ": " (str/join ", " (map issue-label issues)))
    (str "0 " label)))

(defn summarize-active-state
  [{:keys [ready in-progress]}]
  (str (summarize-issue-group "ready" ready)
       "; "
       (summarize-issue-group "in-progress" in-progress)))

(defn active-work?
  [{:keys [ready in-progress]}]
  (or (seq ready)
      (seq in-progress)))

(defn issue-signature
  [issue]
  [(:id issue) (:status issue) (:updated_at issue)])

(defn state-signature
  [{:keys [ready in-progress]}]
  (->> (concat ready in-progress)
       (map issue-signature)
       sort
       vec))

(defn codex-command
  [repo-root prompt]
  (let [codex-bin (env-value "CODEX_BIN" "codex")
        model (System/getenv "CODEX_MODEL")
        profile (System/getenv "CODEX_PROFILE")
        automation-flag (if (truthy-env? "CODEX_BYPASS_APPROVALS" "true")
                          "--dangerously-bypass-approvals-and-sandbox"
                          "--full-auto")]
    (cond-> [codex-bin "exec" automation-flag "-C" repo-root]
      (not (str/blank? model)) (into ["-m" model])
      (not (str/blank? profile)) (into ["-p" profile])
      true (conj prompt))))

(defn run-process!
  [repo-root args]
  (println)
  (println "$" (str/join " " (map shell-quote args)))
  (let [process-builder (doto (ProcessBuilder. ^java.util.List args)
                          (.directory (io/file repo-root))
                          (.redirectInput (ProcessBuilder$Redirect/from (io/file "/dev/null")))
                          (.redirectOutput ProcessBuilder$Redirect/INHERIT)
                          (.redirectError ProcessBuilder$Redirect/INHERIT))
        process (.start process-builder)
        exit-code (.waitFor process)]
    (println "exit:" exit-code)
    exit-code))

(defn run-codex!
  [repo-root prompt]
  (run-process! repo-root (codex-command repo-root prompt)))

(defn sync-beads!
  [repo-root]
  (let [exit-code (run-process! repo-root [(beads-bin) "sync" "--flush-only"])]
    (when-not (zero? exit-code)
      (throw (ex-info "br sync --flush-only failed"
                      {:exit exit-code})))
    true))

(defn ensure-committed!
  [repo-root max-commit-prompts]
  (loop [attempt 1]
    (sync-beads! repo-root)
    (if (git-clean? repo-root)
      true
      (if (> attempt max-commit-prompts)
        (throw (ex-info "Codex left uncommitted changes after commit prompts."
                        {:status (git-status repo-root)
                         :max-commit-prompts max-commit-prompts}))
        (do
          (println)
          (println "Git tree is dirty after Codex run; requesting commit"
                   (str "(" attempt "/" max-commit-prompts ")."))
          (println (git-status repo-root))
          (run-codex! repo-root commit-prompt)
          (recur (inc attempt)))))))

(defn run-loop!
  [options]
  (let [repo-root (git-root)
        max-iterations (parse-int-env "BEADS_CODEX_MAX_ITERATIONS" 100)
        max-codex-failures (parse-int-env "BEADS_CODEX_MAX_FAILURES" 3)
        max-stalled-iterations (parse-int-env "BEADS_CODEX_MAX_STALLED_ITERATIONS" 2)
        max-commit-prompts (parse-int-env "BEADS_CODEX_MAX_COMMIT_PROMPTS" 3)]
    (println "repo:" repo-root)
    (println "limits:"
             {:max-iterations max-iterations
              :max-codex-failures max-codex-failures
              :max-stalled-iterations max-stalled-iterations
              :max-commit-prompts max-commit-prompts})
    (when (:dry-run? options)
      (println "dry-run:" (summarize-active-state (active-state repo-root)))
      (println "git-clean:" (git-clean? repo-root))
      (println "work command:"
               (str/join " " (map shell-quote (codex-command repo-root work-prompt))))
      (println "commit command:"
               (str/join " " (map shell-quote (codex-command repo-root commit-prompt))))
      (println "sync command:"
               (str/join " " (map shell-quote [(beads-bin) "sync" "--flush-only"])))
      (println "push command:"
               (str/join " " (map shell-quote ["git" "push"])))
      (System/exit 0))
    (loop [iteration 1
           consecutive-failures 0
           stalled-iterations 0]
      (let [state-before (active-state repo-root)]
        (cond
          (not (active-work? state-before))
          (println "No ready or in-progress beads remain.")

          (> iteration max-iterations)
          (throw (ex-info "Reached iteration limit with beads work remaining."
                          {:max-iterations max-iterations
                           :state state-before}))

          :else
          (let [head-before (git-head repo-root)
                signature-before (state-signature state-before)
                _ (println)
                _ (println "Iteration" iteration "-" (summarize-active-state state-before))
                exit-code (run-codex! repo-root work-prompt)
                _ (ensure-committed! repo-root max-commit-prompts)
                push-exit-code (run-process! repo-root ["git" "push"])
                _ (when-not (zero? push-exit-code)
                    (println "git push failed; continuing loop."))
                state-after (active-state repo-root)
                signature-after (state-signature state-after)
                head-after (git-head repo-root)
                progressed? (or (not= head-before head-after)
                                (not= signature-before signature-after))
                next-failures (if (or (zero? exit-code) progressed?)
                                0
                                (inc consecutive-failures))
                next-stalled (if progressed?
                               0
                               (inc stalled-iterations))]
            (println "Post-run:" (summarize-active-state state-after))
            (println "Progress:" (if progressed? "yes" "no"))
            (when (> next-failures max-codex-failures)
              (throw (ex-info "Codex failed too many times in a row."
                              {:max-codex-failures max-codex-failures
                               :last-exit exit-code
                               :state state-after})))
            (when (> next-stalled max-stalled-iterations)
              (throw (ex-info "No git or beads progress detected for too many iterations."
                              {:max-stalled-iterations max-stalled-iterations
                               :state state-after})))
            (recur (inc iteration) next-failures next-stalled)))))))

(defn parse-args
  [args]
  (loop [remaining args
         options {}]
    (if-let [arg (first remaining)]
      (case arg
        "--dry-run" (recur (rest remaining) (assoc options :dry-run? true))
        "--help" (assoc options :help? true)
        "-h" (assoc options :help? true)
        (throw (ex-info (str "Unknown argument: " arg) {:arg arg})))
      options)))

(try
  (let [options (parse-args *command-line-args*)]
    (if (:help? options)
      (println usage)
      (run-loop! options)))
  (catch Throwable throwable
    (binding [*out* *err*]
      (println "codex-beads-loop failed:" (ex-message throwable))
      (when-let [data (ex-data throwable)]
        (println data)))
    (System/exit 1)))
