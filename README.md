# IncludeDamon
**In**-**Clu**ster **De**ployment and **Da**emonSet **Mo**nitor
### Usage:
```yaml
apiVersion: v1
kind: ServiceAccount
metadata:
  name: monitoring-serviceaccount
  namespace: monitoring
---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: monitoring-serviceaccount
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: cluster-admin
subjects:
- kind: ServiceAccount
  name: monitoring-serviceaccount
  namespace: monitoring
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: internal-monitor
  namespace: monitoring
  labels:
    app: internal-monitor
spec:
  replicas: 1
  strategy:
    type: Recreate
  selector:
    matchLabels:
      app: internal-monitor
  template:
    metadata:
      labels:
        app: internal-monitor
        kind: system
    spec:
      serviceAccountName: monitoring-serviceaccount
      containers:
      - name: internal-monitor
        image: ghcr.io/sojaner/includedamon:latest
        imagePullPolicy: Always
        resources:
          limits:
            memory: 256Mi
            cpu: 256m
        env:
        - name: SLACK_WEBHOOK_URL
          valueFrom:
              configMapKeyRef:
                name: slack-channel
                key: webhook
        - name: TARGETS
          value: |
            default/daemonset/application1|https://domain.com|/check,/monitor|run=application1|https|post|{"something": "value"}|application/json
            default/daemonset/application2|https://api.domain.com|/monitor|run=application2|http
            default/daemonset/application3|https://data.domain.com|/check|run=application3
      restartPolicy: Always
```
