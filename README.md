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
            [
              {
                "namespace": "default",
                "resourceType": "daemonset",
                "resourceName": "application1",
                "host": "https://domain.com",
                "paths": ["/check", "/monitor"],
                "labelSelector": "run=application1",
                "scheme": "https",
                "verb": "POST",
                "payload": "{\"something\":\"value\"}",
                "contentType": "application/json",
                "timeoutSeconds": 10
              },
              {
                "namespace": "default",
                "resourceType": "daemonset",
                "resourceName": "application2",
                "host": "https://api.domain.com",
                "paths": ["/monitor"],
                "timeoutSeconds": 5,
                "labelSelector": "run=application2"
              },
              {
                "namespace": "default",
                "resourceType": "daemonset",
                "resourceName": "application3",
                "host": "https://data.domain.com",
                "paths": ["/check"],
                "labelSelector": "use=application3"
              }
            ]
      restartPolicy: Always
```

`TARGETS` is now a JSON array; each entry must include `namespace`, `resourceType`
(daemonset/deployment), `resourceName`, and `host`. Optional fields:
`paths` (defaults to `["/"]`), `labelSelector` (required; no global format and no workload fallback),
spec selector), `scheme` (`http`/`https`, defaults to the host's scheme), `verb` (`GET`/`POST`),
`payload`/`contentType` (required when `verb` is `POST`), `timeoutSeconds` (defaults to `5`),
`hostHeader` (defaults to host[:port]), `issueWindowSeconds` (defaults to `60`),
`startupWindowSeconds` (defaults to `120`), `resourceIssueWindowSeconds` (defaults to `300`),
`restartThreshold` (defaults to `0.9`), and `destroyFaultyPods` (defaults to `false`).
