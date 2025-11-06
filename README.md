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
        - name: LABEL_SELECTOR_FORMAT
          value: 'use="{0}"'
        - name: TARGETS
          value: 'default/daemonset/application1|http://domain.com|/check;default/daemonset/application2|http://api.domain.com|/monitor'
      restartPolicy: Always
```
